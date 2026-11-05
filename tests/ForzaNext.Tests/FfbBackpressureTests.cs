using ForzaNext.Core.FFB;
using ForzaNext.Core.Virtual;
using SharpDX.DirectInput;
using Xunit;

namespace ForzaNext.Tests;

public sealed class FfbBackpressureTests
{
    private sealed class Sink : IFfbEffectSink
    {
        public List<(byte Id, int Magnitude, bool Start)> Applied = new();
        public List<byte> Stopped = new(), Freed = new();
        public void Apply(byte id, FfbEffectState state, byte gain, bool start) => Applied.Add((id, state.Constant?.Magnitude ?? 0, start));
        public void Stop(byte id) => Stopped.Add(id);
        public void Free(byte id) => Freed.Add(id);
        public void Control(FFB_CTRL control) { }
    }
    private static FfbPacket Report(byte id, FFBEType type = FFBEType.ET_CONST) => new(FFBPType.PT_EFFREP, new FfbEffReport { EffectBlockIndex = id, EffectType = type, Gain = 255, Duration = 65535 });
    private static FfbPacket Constant(byte id, int magnitude) => new(FFBPType.PT_CONSTREP, new FfbEffConstant { EffectBlockIndex = id, Magnitude = magnitude });
    private static FfbPacket Op(byte id, FFBOP op) => new(FFBPType.PT_EFOPREP, new FfbEffOp { EffectBlockIndex = id, EffectOp = op, LoopCount = 1 });

    [Fact]
    public void RacingBurstKeepsNewestForceWithoutGrowingQueue()
    {
        var inbox = new FfbInbox(8); var packets = new List<FfbPacket>();
        Assert.True(inbox.Enqueue(Report(1))); Assert.True(inbox.Enqueue(Constant(1, 1))); Assert.True(inbox.Enqueue(Op(1, FFBOP.EFF_START)));
        for (int i = 0; i < 200000; i++) { Assert.True(inbox.Enqueue(Report(1))); Assert.True(inbox.Enqueue(Constant(1, i % 10000))); }
        Assert.Equal(5, inbox.Count); Assert.Equal(399998, inbox.Coalesced);
        inbox.Drain(packets);
        var sink = new Sink(); new FfbProtocol(sink).ProcessBatch(packets);
        Assert.Equal(((byte)1, 9999, true), Assert.Single(sink.Applied));
        Assert.Equal(0, inbox.Count);
    }
    [Fact]
    public void ParameterReplacementCannotCrossStopOrFree()
    {
        var inbox = new FfbInbox(); var packets = new List<FfbPacket>();
        foreach (var packet in new[] { Constant(1, 100), Op(1, FFBOP.EFF_STOP), Constant(1, 200), new FfbPacket(FFBPType.PT_BLKFRREP, 1), Constant(1, 300) }) Assert.True(inbox.Enqueue(packet));
        inbox.Drain(packets);
        Assert.Equal(5, packets.Count);
        Assert.Equal(100, ((FfbEffConstant)packets[0].Value).Magnitude);
        Assert.Equal(200, ((FfbEffConstant)packets[2].Value).Magnitude);
        Assert.Equal(300, ((FfbEffConstant)packets[4].Value).Magnitude);
    }
    [Fact]
    public void TypeChangeIsKeptBetweenOldAndNewParameters()
    {
        var inbox = new FfbInbox(); var packets = new List<FfbPacket>();
        foreach (var packet in new[] { Report(1), Constant(1, 100), Report(1, FFBEType.ET_RAMP), Constant(1, 200) }) Assert.True(inbox.Enqueue(packet));
        inbox.Drain(packets);
        Assert.Equal(4, packets.Count); Assert.Equal(FFBEType.ET_CONST, ((FfbEffReport)packets[0].Value).EffectType);
        Assert.Equal(FFBEType.ET_RAMP, ((FfbEffReport)packets[2].Value).EffectType);
    }
    [Theory]
    [InlineData(FFB_CTRL.CTRL_STOPALL)] [InlineData(FFB_CTRL.CTRL_DISACT)] [InlineData(FFB_CTRL.CTRL_DEVRST)]
    public void StopControlsWinOverDirtyBatchedForce(FFB_CTRL control)
    {
        var sink = new Sink(); var protocol = new FfbProtocol(sink);
        protocol.ProcessBatch(new[] { Report(1), Constant(1, 1000), Op(1, FFBOP.EFF_START), new FfbPacket(FFBPType.PT_CTRLREP, control), Constant(1, 9999) });
        Assert.Empty(sink.Applied); Assert.Contains((byte)1, sink.Stopped);
    }
    [Fact]
    public void LifecycleCommandOverflowIsExplicitAndBounded()
    {
        var inbox = new FfbInbox(2);
        Assert.True(inbox.Enqueue(Op(1, FFBOP.EFF_START))); Assert.True(inbox.Enqueue(Op(1, FFBOP.EFF_STOP)));
        Assert.False(inbox.Enqueue(Op(1, FFBOP.EFF_START))); Assert.Equal(2, inbox.Count);
    }
    [Fact]
    public void DrainingOldSegmentDoesNotLoseLaterSegmentReplacementKey()
    {
        var inbox = new FfbInbox(); var packets = new List<FfbPacket>();
        inbox.Enqueue(Constant(1, 1)); inbox.Enqueue(Op(1, FFBOP.EFF_STOP)); inbox.Enqueue(Constant(1, 2));
        inbox.Drain(packets, 1); inbox.Enqueue(Constant(1, 3)); inbox.Drain(packets);
        Assert.Equal(2, packets.Count); Assert.Equal(3, ((FfbEffConstant)packets[1].Value).Magnitude);
    }
    [Fact]
    public void ConstantMagnitudeUpdateWritesOnlySpecificParameters()
    {
        EffectParameters Parameters(int magnitude, int gain = 10000) => new() { Duration = -1, Gain = gain, Directions = new[] { 0 }, Parameters = new ConstantForce { Magnitude = magnitude } };
        Assert.Equal(EffectParameterFlags.TypeSpecificParameters, FfbParameterChanges.Between(Parameters(1), Parameters(2)));
        Assert.Equal((EffectParameterFlags)0, FfbParameterChanges.Between(Parameters(2), Parameters(2)));
        Assert.Equal(EffectParameterFlags.Gain, FfbParameterChanges.Between(Parameters(2), Parameters(2, 5000)));
    }
    [Fact]
    public void UnchangedEnvelopesDoNotCauseAnotherUsbReport()
    {
        EffectParameters Parameters() => new() { Directions = new[] { 0 }, Parameters = new ConstantForce { Magnitude = 1 }, Envelope = new() { AttackLevel = 100, AttackTime = 5000, FadeLevel = 100, FadeTime = 5000 } };
        var previous = Parameters(); var next = Parameters();
        Assert.Equal((EffectParameterFlags)0, FfbParameterChanges.Between(previous, next));
        next.Envelope.AttackTime = 6000;
        Assert.Equal(EffectParameterFlags.Envelope, FfbParameterChanges.Between(previous, next));
    }
    [Fact]
    public void BatchUpdatesDoNotRestartPlayingEffect()
    {
        var sink = new Sink(); var protocol = new FfbProtocol(sink);
        protocol.ProcessBatch(new[] { Report(1), Constant(1, 10), Op(1, FFBOP.EFF_START) });
        protocol.ProcessBatch(new[] { Constant(1, 20), Constant(1, 30) });
        Assert.Equal(2, sink.Applied.Count); Assert.Equal(((byte)1, 30, false), sink.Applied[1]);
    }
    [Fact]
    public void BothConditionAxesHaveIndependentReplacementSlots()
    {
        var inbox = new FfbInbox(); var packets = new List<FfbPacket>();
        inbox.Enqueue(new(FFBPType.PT_CONDREP, new FfbEffCond { EffectBlockIndex = 1, isY = 0, PosCoeff = 100 }));
        inbox.Enqueue(new(FFBPType.PT_CONDREP, new FfbEffCond { EffectBlockIndex = 1, isY = 1, PosCoeff = 200 }));
        inbox.Enqueue(new(FFBPType.PT_CONDREP, new FfbEffCond { EffectBlockIndex = 1, isY = 0, PosCoeff = 300 }));
        inbox.Drain(packets); Assert.Equal(2, packets.Count);
        Assert.Equal(300, ((FfbEffCond)packets[0].Value).PosCoeff); Assert.Equal(200, ((FfbEffCond)packets[1].Value).PosCoeff);
    }
    [Fact]
    public async Task ProducerAndConsumerKeepLatestValueUnderContention()
    {
        var inbox = new FfbInbox(8); int last = -1;
        var producer = Task.Run(() => { for (int i = 0; i < 100000; i++) Assert.True(inbox.Enqueue(Constant(1, i))); });
        var packets = new List<FfbPacket>();
        while (!producer.IsCompleted || inbox.Count > 0)
        {
            inbox.Drain(packets);
            foreach (var packet in packets) { int current = ((FfbEffConstant)packet.Value).Magnitude; Assert.True(current > last); last = current; }
            await Task.Yield();
        }
        await producer; Assert.Equal(99999, last);
    }
}
