using System.Runtime.InteropServices;
using ForzaNext.Core;
using ForzaNext.Core.Config;
using ForzaNext.Core.Devices;
using ForzaNext.Core.FFB;
using ForzaNext.Core.Virtual;
using Xunit;

namespace ForzaNext.Tests;

public class RegressionTests
{
    private static ControllerProfile Profile() => new()
    {
        InstanceGuid = Guid.NewGuid(), ProductGuid = Guid.NewGuid(), InstanceName = "Test controller", ProductName = "Test product"
    };

    [Fact]
    public void ReportMatchesNativeAbiAndNeutralDoesNotPressButtons()
    {
        Assert.Equal(108, Marshal.SizeOf<JoystickPositionV2>());
        Assert.Equal(76, Marshal.OffsetOf<JoystickPositionV2>(nameof(JoystickPositionV2.Buttons)).ToInt32());
        Assert.Equal(80, Marshal.OffsetOf<JoystickPositionV2>(nameof(JoystickPositionV2.bHats)).ToInt32());
        Assert.Equal(96, Marshal.OffsetOf<JoystickPositionV2>(nameof(JoystickPositionV2.ButtonsEx1)).ToInt32());
        var memory = Marshal.AllocHGlobal(108);
        try
        {
            Marshal.StructureToPtr(InputAggregator.NeutralPosition(), memory, false);
            Assert.Equal(0, Marshal.ReadInt32(memory, 76));
            Assert.Equal(-1, Marshal.ReadInt32(memory, 80));
            Assert.Equal(-1, Marshal.ReadInt32(memory, 92));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [Theory]
    [InlineData(typeof(FfbEffReport), 28)]
    [InlineData(typeof(FfbEffConstant), 8)]
    [InlineData(typeof(FfbEffCond), 32)]
    [InlineData(typeof(FfbEffOp), 12)]
    [InlineData(typeof(FfbEffPeriod), 20)]
    [InlineData(typeof(FfbEffRamp), 12)]
    [InlineData(typeof(FfbEffEnvelope), 20)]
    public void FfbStructuresMatchNativeSizes(Type type, int size) => Assert.Equal(size, Marshal.SizeOf(type));

    [Theory]
    [InlineData(nameof(FfbEffReport.StartDelay), 14)]
    [InlineData(nameof(FfbEffReport.Gain), 16)]
    [InlineData(nameof(FfbEffReport.AxesEnabledDirection), 18)]
    [InlineData(nameof(FfbEffReport.Polar), 20)]
    [InlineData(nameof(FfbEffReport.Direction), 24)]
    [InlineData(nameof(FfbEffReport.DirY), 26)]
    public void BrunnerReportFieldsMatchInstalledNativeDecoder(string field, int offset) => Assert.Equal(offset, Marshal.OffsetOf<FfbEffReport>(field).ToInt32());

    [Fact]
    public void VirtualButtonRequirementIncludesAllProfilesAndIgnoresPovActions()
    {
        var wheel = Profile();
        wheel.Buttons = new() { new() { Id = 17, Index = 0 }, new() { Id = 32, Index = 1 } };
        var shifter = Profile();
        shifter.Buttons = new() { new() { Id = 26, Index = 0 } };
        Assert.Equal(27, VirtualWheelDriver.RequiredButtonCount(new[] { wheel, shifter }));
    }

    [Fact]
    public void NativeCallbackUsesWindowsCallingConvention()
    {
        var attribute = typeof(NativeVJoy.FfbCallback).GetCustomAttributes(typeof(UnmanagedFunctionPointerAttribute), false)
            .Cast<UnmanagedFunctionPointerAttribute>().Single();
        Assert.Equal(CallingConvention.Winapi, attribute.CallingConvention);
        var parameters = typeof(NativeVJoy).GetMethod(nameof(NativeVJoy.DriverMatch))!.GetParameters();
        Assert.All(parameters, p => Assert.Equal(typeof(ushort).MakeByRefType(), p.ParameterType));
    }

    [Fact]
    public void InvalidReadNeutralizesInvertedPedalSteeringButtonsAndPovs()
    {
        var p = Profile();
        p.Axes = new() { new() { Id = "Steering", AxisIndex = 0 }, new() { Id = "Throttle", AxisIndex = 1, Inverted = 1 } };
        p.Buttons = new() { new() { Id = 17, Index = 0 } };
        p.DPad = new() { Index = 0 };
        var aggregator = new InputAggregator(new() { p });
        var state = aggregator.GetStateBuffer(p.InstanceGuid);
        Assert.Equal(16384, aggregator.Aggregate().AxisX);
        Assert.Equal(1, aggregator.Aggregate().AxisZ);
        state.IsValid = true; state.Buttons[0] = true; state.PointOfView[0] = 9000;
        Assert.Equal(32768, aggregator.Aggregate().AxisZ);
        Assert.NotEqual(0u, aggregator.Aggregate().Buttons);
        state.Invalidate();
        var neutral = aggregator.Aggregate();
        Assert.Equal(1, neutral.AxisZ); Assert.Equal(16384, neutral.AxisX);
        Assert.Equal(0u, neutral.Buttons); Assert.Equal(uint.MaxValue, neutral.bHats);
        Assert.All(state.PointOfView, value => Assert.Equal(-1, value));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(65535, 32768)]
    [InlineData(-100, 1)]
    [InlineData(70000, 32768)]
    public void PedalRangeIsClamped(int raw, int expected)
    {
        var p = Profile(); p.Axes = new() { new() { Id = "Brake", AxisIndex = 0 } };
        var aggregator = new InputAggregator(new() { p });
        var state = aggregator.GetStateBuffer(p.InstanceGuid); state.IsValid = true; state.Axes[0] = raw;
        Assert.Equal(expected, aggregator.Aggregate().AxisXRot);
    }

    [Fact]
    public void AggregatorOwnsSnapshotAndUsesConfiguredPov()
    {
        var p = Profile(); p.Axes = new() { new() { Id = "Throttle", AxisIndex = 0 } }; p.DPad = new() { Index = 2 };
        var aggregator = new InputAggregator(new() { p });
        p.Axes.Clear(); p.DPad.Index = 0;
        var state = aggregator.GetStateBuffer(p.InstanceGuid); state.IsValid = true;
        state.Axes[0] = 65535; state.PointOfView[0] = 0; state.PointOfView[2] = 18000;
        Assert.Equal(32768, aggregator.Aggregate().AxisZ); Assert.Equal(18000u, aggregator.Aggregate().bHats);
    }

    [Fact]
    public void RbDoesNotActivateHandbrakeAndDigitalHandbrakeOverridesSeparateAnalogDevice()
    {
        var p = Profile(); p.Buttons = new() { new() { Id = 7, Index = 0 }, new() { Id = 26, Index = 1 }, new() { Id = 127, Index = 2 } };
        var analog = Profile(); analog.Axes = new() { new() { Id = "Handbrake", AxisIndex = 0 } };
        var aggregator = new InputAggregator(new() { p, analog });
        var state = aggregator.GetStateBuffer(p.InstanceGuid); state.IsValid = true; state.Buttons[0] = true;
        aggregator.GetStateBuffer(analog.InstanceGuid).IsValid = true;
        Assert.Equal(1, aggregator.Aggregate().AxisZRot);
        state.Buttons[1] = true; state.Buttons[2] = true;
        Assert.Equal(32768, aggregator.Aggregate().AxisZRot);
        Assert.Equal(0x80000000u, aggregator.Aggregate().ButtonsEx3);
    }

    [Fact]
    public void OppositeDpadDirectionsCancel()
    {
        var p = Profile(); p.Buttons = new() { new() { Id = 32, Index = 0 }, new() { Id = 33, Index = 1 } };
        var aggregator = new InputAggregator(new() { p });
        var state = aggregator.GetStateBuffer(p.InstanceGuid); state.IsValid = true;
        state.Buttons[0] = state.Buttons[1] = true;
        Assert.Equal(uint.MaxValue, aggregator.Aggregate().bHats);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-0.01)]
    [InlineData(double.NaN)]
    public void InvalidDeadzonesAreRejected(double deadzone)
    {
        var p = Profile(); p.Axes = new() { new() { Id = "Steering", AxisIndex = 0, Deadzone = deadzone } };
        Assert.Throws<InvalidDataException>(() => ProfileManager.Validate(new() { p }));
    }

    [Fact]
    public void DuplicateActionsAcrossDevicesAreRejected()
    {
        var first = Profile(); var second = Profile();
        first.Buttons = new() { new() { Id = 17, Index = 0 } };
        second.Buttons = new() { new() { Id = 17, Index = 1 } };
        Assert.Throws<InvalidDataException>(() => ProfileManager.Validate(new() { first, second }));
    }

    [Fact]
    public void ProfileRoundTripPreservesFfbAndCreatesRecoverableBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ForzaNextTests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profile.json");
        try
        {
            var p = Profile(); p.FFBParameters = new() { InvertConstantForce = false, Sine = new()
            { Frequency = 0.6, Phase = 0.2, GearShiftVibrations = new() { Frequency = 1.7, Strength = 0.2 } } };
            ProfileManager.SaveToFile(path, new() { p });
            p.FFBParameters.InvertConstantForce = true;
            ProfileManager.SaveToFile(path, new() { p });
            Assert.True(ProfileManager.LoadFromFile(path)[0].FFBParameters!.InvertConstantForce);
            var backup = ProfileManager.LoadFromFile(path + ".bak")[0].FFBParameters!;
            Assert.False(backup.InvertConstantForce);
            Assert.Equal(1.7, backup.Sine!.GearShiftVibrations!.Frequency);
            Assert.Equal(0.6, DirectInputFFBBridge.FromProfile(backup).SineFrequency);
            Assert.False(DirectInputFFBBridge.FromProfile(backup).InvertConstantForce);
            p.Axes = new() { new() { Id = "Invalid" } };
            Assert.Throws<InvalidDataException>(() => ProfileManager.SaveToFile(path, new() { p }));
            Assert.True(ProfileManager.LoadFromFile(path)[0].FFBParameters!.InvertConstantForce);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ReconnectUsesUniqueProductAndRejectsAmbiguity()
    {
        var p = Profile(); var replacement = Guid.NewGuid();
        var device = new ConnectedDeviceInfo { InstanceGuid = replacement, ProductGuid = p.ProductGuid, InstanceName = "Connected", ProductName = "Product" };
        DirectInputDeviceManager.ResolveProfiles(new() { p }, new[] { device });
        Assert.Equal(replacement, p.InstanceGuid);
        p.InstanceGuid = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => DirectInputDeviceManager.ResolveProfiles(new() { p }, new[]
        { device, new ConnectedDeviceInfo { ProductGuid = p.ProductGuid, InstanceGuid = Guid.NewGuid() } }));
    }

    private sealed class Sink : IFfbEffectSink
    {
        public readonly List<(byte Id, FFBEType Type, int Magnitude, byte Gain, bool Start, int Iterations)> Applied = new();
        public readonly List<byte> Stopped = new();
        public readonly List<byte> Freed = new();
        public void Apply(byte id, FfbEffectState state, byte gain, bool start) => Applied.Add((id, state.Report!.Value.EffectType, state.Constant?.Magnitude ?? 0, gain, start, state.Iterations));
        public void Stop(byte id) => Stopped.Add(id);
        public void Free(byte id) => Freed.Add(id);
        public void Control(FFB_CTRL control) { }
    }
    private static FfbPacket Report(byte id, FFBEType type) => new(FFBPType.PT_EFFREP, new FfbEffReport { EffectBlockIndex = id, EffectType = type, Gain = 255, Duration = 100 });
    private static FfbPacket Constant(byte id, int value) => new(FFBPType.PT_CONSTREP, new FfbEffConstant { EffectBlockIndex = id, Magnitude = value });
    private static FfbPacket Op(byte id, FFBOP op, byte loops = 1) => new(FFBPType.PT_EFOPREP, new FfbEffOp { EffectBlockIndex = id, EffectOp = op, LoopCount = loops });

    [Fact]
    public void ParametersDoNotStartTorqueAndInterleavedBlocksRemainIndependent()
    {
        var sink = new Sink(); var protocol = new FfbProtocol(sink);
        protocol.Process(Report(1, FFBEType.ET_CONST)); protocol.Process(Constant(1, 2300));
        protocol.Process(Report(2, FFBEType.ET_SPRNG));
        protocol.Process(new(FFBPType.PT_CONDREP, new FfbEffCond { EffectBlockIndex = 2, PosCoeff = 5000 }));
        Assert.Empty(sink.Applied);
        protocol.Process(Op(1, FFBOP.EFF_START, 3));
        Assert.Equal((byte)1, sink.Applied.Single().Id);
        Assert.Equal(FFBEType.ET_CONST, sink.Applied.Single().Type);
        Assert.Equal(2300, sink.Applied.Single().Magnitude);
        Assert.Equal(3, sink.Applied.Single().Iterations);
        protocol.Process(Op(1, FFBOP.EFF_STOP));
        protocol.Process(Constant(1, 9000));
        Assert.Single(sink.Applied); Assert.Equal(new byte[] { 1 }, sink.Stopped);
    }

    [Fact]
    public void SoloGainAndBlockFreeHaveIndependentState()
    {
        var sink = new Sink(); var protocol = new FfbProtocol(sink);
        foreach (byte id in new byte[] { 1, 2 })
        { protocol.Process(Report(id, FFBEType.ET_CONST)); protocol.Process(Constant(id, id * 100)); protocol.Process(Op(id, FFBOP.EFF_START)); }
        protocol.Process(Op(2, FFBOP.EFF_SOLO, 0));
        Assert.Contains((byte)1, sink.Stopped);
        Assert.Equal(-1, sink.Applied.Last().Iterations);
        protocol.Process(new(FFBPType.PT_GAINREP, (byte)64));
        Assert.Equal((byte)2, sink.Applied.Last().Id); Assert.Equal((byte)64, sink.Applied.Last().Gain);
        Assert.False(sink.Applied.Last().Start);
        protocol.Process(new(FFBPType.PT_BLKFRREP, 2));
        int count = sink.Applied.Count;
        protocol.Refresh();
        Assert.Equal(count, sink.Applied.Count); Assert.Contains((byte)2, sink.Freed);
    }

    [Theory]
    [InlineData(FFB_CTRL.CTRL_STOPALL)]
    [InlineData(FFB_CTRL.CTRL_DISACT)]
    [InlineData(FFB_CTRL.CTRL_DEVRST)]
    public void StopControlCannotBeUndoneByParameterUpdates(FFB_CTRL control)
    {
        var sink = new Sink(); var protocol = new FfbProtocol(sink);
        protocol.Process(Report(1, FFBEType.ET_CONST)); protocol.Process(Constant(1, 2000)); protocol.Process(Op(1, FFBOP.EFF_START));
        protocol.Process(new(FFBPType.PT_CTRLREP, control));
        protocol.Process(Constant(1, 8000)); protocol.Refresh();
        Assert.Single(sink.Applied); Assert.Contains((byte)1, sink.Stopped);
    }

    [Fact]
    public void PauseDoesNotAllowNewStarts()
    {
        var sink = new Sink(); var protocol = new FfbProtocol(sink);
        protocol.Process(Report(1, FFBEType.ET_CONST)); protocol.Process(Constant(1, 2000));
        protocol.Process(new(FFBPType.PT_CTRLREP, FFB_CTRL.CTRL_DEVPAUSE));
        protocol.Process(Op(1, FFBOP.EFF_START)); Assert.Empty(sink.Applied);
        protocol.Process(new(FFBPType.PT_CTRLREP, FFB_CTRL.CTRL_DEVCONT));
        protocol.Process(Op(1, FFBOP.EFF_START)); Assert.Single(sink.Applied);
    }

    [Fact]
    public async Task FailedStartupCompletesAndCanBeStoppedWithoutNativeHardware()
    {
        var session = new WheelSession(new(), IntPtr.Zero, new());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Started);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Completion.IsCompleted);
        Assert.Equal(0u, session.Telemetry.Position.Buttons);
        Assert.Equal(1, session.Telemetry.Position.AxisZ);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.TestAsync(MotorTest.Rack));
    }
}
