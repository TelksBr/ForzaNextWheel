using ForzaNext.Core.Virtual;

namespace ForzaNext.Core.FFB;

public sealed record FfbPacket(FFBPType Type, object Value)
{
    // Native packet memory is valid only during the callback. Copy every field now.
    public static FfbPacket? CopyFromNative(IntPtr data, FFBPType type)
    {
        switch (type)
        {
            case FFBPType.PT_EFFREP:
                var r = new FfbEffReport(); return NativeVJoy.Ffb_h_Eff_Report(data, ref r) == 0 ? new(type, r) : null;
            case FFBPType.PT_CONSTREP:
                var c = new FfbEffConstant(); return NativeVJoy.Ffb_h_Eff_Constant(data, ref c) == 0 ? new(type, c) : null;
            case FFBPType.PT_CONDREP:
                var d = new FfbEffCond(); return NativeVJoy.Ffb_h_Eff_Cond(data, ref d) == 0 ? new(type, d) : null;
            case FFBPType.PT_PRIDREP:
                var p = new FfbEffPeriod(); return NativeVJoy.Ffb_h_Eff_Period(data, ref p) == 0 ? new(type, p) : null;
            case FFBPType.PT_RAMPREP:
                var ramp = new FfbEffRamp(); return NativeVJoy.Ffb_h_Eff_Ramp(data, ref ramp) == 0 ? new(type, ramp) : null;
            case FFBPType.PT_ENVREP:
                var e = new FfbEffEnvelope(); return NativeVJoy.Ffb_h_Eff_Envlp(data, ref e) == 0 ? new(type, e) : null;
            case FFBPType.PT_EFOPREP:
                var op = new FfbEffOp(); return NativeVJoy.Ffb_h_EffOp(data, ref op) == 0 ? new(type, op) : null;
            case FFBPType.PT_CTRLREP:
                FFB_CTRL ctrl = 0; return NativeVJoy.Ffb_h_DevCtrl(data, ref ctrl) == 0 ? new(type, ctrl) : null;
            case FFBPType.PT_GAINREP:
                byte gain = 0; return NativeVJoy.Ffb_h_DevGain(data, ref gain) == 0 ? new(type, gain) : null;
            case FFBPType.PT_BLKFRREP:
                int index = 0; return NativeVJoy.Ffb_h_EBI(data, ref index) == 0 ? new(type, index) : null;
            default: return null;
        }
    }
}

public sealed class FfbEffectState
{
    public FfbEffReport? Report { get; set; }
    public FfbEffConstant? Constant { get; set; }
    public FfbEffPeriod? Periodic { get; set; }
    public FfbEffCond? Condition { get; set; }
    public FfbEffRamp? Ramp { get; set; }
    public FfbEffEnvelope? Envelope { get; set; }
    public bool Running { get; set; }
    public int Iterations { get; set; } = 1;
    public bool StartPending { get; set; }
    public bool Ready => Report?.EffectType switch
    {
        FFBEType.ET_CONST => Constant != null,
        FFBEType.ET_RAMP => Ramp != null,
        FFBEType.ET_SINE or FFBEType.ET_SQR or FFBEType.ET_TRNGL or FFBEType.ET_STUP or FFBEType.ET_STDN => Periodic != null,
        FFBEType.ET_SPRNG or FFBEType.ET_DMPR or FFBEType.ET_INRT or FFBEType.ET_FRCTN => Condition != null,
        _ => false
    };
}

public interface IFfbEffectSink
{
    void Apply(byte index, FfbEffectState effect, byte gain, bool start);
    void Stop(byte index);
    void Free(byte index);
    void Control(FFB_CTRL control);
}

// Single-threaded protocol reducer. Each block owns its report, parameters and playback state.
public sealed class FfbProtocol
{
    private readonly IFfbEffectSink _sink;
    private readonly Dictionary<byte, FfbEffectState> _effects = new();
    private readonly HashSet<byte> _stoppedBlocks = new();
    private bool _deviceReset;
    private bool _enabled = true;
    private bool _paused;
    private byte _gain = 255;
    private bool _batch;
    private readonly HashSet<byte> _dirty = new();
    public FfbProtocol(IFfbEffectSink sink) => _sink = sink;

    public void ProcessBatch(IEnumerable<FfbPacket> packets)
    {
        if (_batch) throw new InvalidOperationException("Lote FFB já está em processamento.");
        _batch = true;
        try { foreach (var packet in packets) Process(packet); }
        finally { _batch = false; }
        try
        {
            foreach (byte id in _dirty)
                if (_effects.TryGetValue(id, out var state) && state.Running && state.Ready && _enabled && !_paused)
                    Apply(id, state);
        }
        finally { _dirty.Clear(); }
    }
    private void Apply(byte index, FfbEffectState state)
    {
        if (_batch) { _dirty.Add(index); return; }
        _sink.Apply(index, state, _gain, state.StartPending);
        state.StartPending = false;
    }

    public void Process(FfbPacket packet)
    {
        if (packet.Value is FFB_CTRL control)
        {
            switch (control)
            {
                case FFB_CTRL.CTRL_STOPALL:
                case FFB_CTRL.CTRL_DISACT:
                case FFB_CTRL.CTRL_DEVRST:
                    StopAll();
                    if (control == FFB_CTRL.CTRL_DISACT) _enabled = false;
                    if (control == FFB_CTRL.CTRL_DEVRST)
                    {
                        _deviceReset = true;
                        foreach (var id in _effects.Keys) _sink.Free(id);
                        _effects.Clear(); _gain = 255; _enabled = true; _paused = false;
                    }
                    break;
                case FFB_CTRL.CTRL_ENACT: _enabled = true; _deviceReset = false; break;
                case FFB_CTRL.CTRL_DEVPAUSE: _paused = true; break;
                case FFB_CTRL.CTRL_DEVCONT: _paused = false; break;
            }
            _sink.Control(control);
            if (control == FFB_CTRL.CTRL_DEVCONT) Refresh();
            return;
        }
        if (packet.Type == FFBPType.PT_GAINREP && packet.Value is byte gain)
        {
            _gain = gain;
            Refresh();
            return;
        }
        if (packet.Type == FFBPType.PT_BLKFRREP && packet.Value is int freed)
        {
            if (freed is > 0 and <= 255)
            {
                _sink.Free((byte)freed);
                _effects.Remove((byte)freed);
                _stoppedBlocks.Remove((byte)freed);
            }
            return;
        }
        byte index = packet.Value switch
        {
            FfbEffReport r => r.EffectBlockIndex, FfbEffConstant c => c.EffectBlockIndex,
            FfbEffPeriod p => p.EffectBlockIndex, FfbEffCond c => c.EffectBlockIndex,
            FfbEffRamp r => r.EffectBlockIndex, FfbEffEnvelope e => e.EffectBlockIndex,
            FfbEffOp o => o.EffectBlockIndex, _ => (byte)0
        };
        if (index == 0) return;
        if (!_effects.TryGetValue(index, out var state)) _effects[index] = state = new();
        switch (packet.Value)
        {
            case FfbEffReport r:
                FfbDebugLogger.Log("PROTO-REPORT", $"Block={index} Type={r.EffectType} Dur={r.Duration} Gain={r.Gain} Dir={r.Direction} Polar={r.Polar}");
                _deviceReset = false;
                _stoppedBlocks.Remove(index);
                if (state.Report is { } old && old.EffectType != r.EffectType)
                {
                    _sink.Free(index);
                    _effects[index] = state = new();
                }
                state.Report = r; break;
            case FfbEffConstant c:
                FfbDebugLogger.Log("PROTO-CONST", $"Block={index} Mag={(short)c.Magnitude} (Raw={c.Magnitude})");
                if (state.Report == null && !_stoppedBlocks.Contains(index) && !_deviceReset)
                {
                    state.Report = new FfbEffReport { EffectBlockIndex = index, EffectType = FFBEType.ET_CONST, Duration = ushort.MaxValue, Gain = 255, Polar = 1, Direction = 64 };
                    state.Running = true;
                    state.StartPending = true;
                    state.Iterations = -1;
                }
                state.Constant = c; break;
            case FfbEffPeriod p:
                FfbDebugLogger.Log("PROTO-PERIOD", $"Block={index} Mag={(ushort)p.Magnitude} Period={p.Period} Phase={p.Phase} Offset={(short)p.Offset}");
                if (state.Report == null && !_stoppedBlocks.Contains(index) && !_deviceReset)
                {
                    state.Report = new FfbEffReport { EffectBlockIndex = index, EffectType = FFBEType.ET_SINE, Duration = ushort.MaxValue, Gain = 255, Polar = 1, Direction = 64 };
                    state.Running = true;
                    state.StartPending = true;
                    state.Iterations = -1;
                }
                state.Periodic = p; break;
            case FfbEffCond c when c.isY == 0:
                FfbDebugLogger.Log("PROTO-COND", $"Block={index} Center={(short)c.CenterPointOffset} Dead={(ushort)c.DeadBand} PosCoeff={(short)c.PosCoeff} NegCoeff={(short)c.NegCoeff}");
                if (state.Report == null && !_stoppedBlocks.Contains(index) && !_deviceReset)
                {
                    state.Report = new FfbEffReport { EffectBlockIndex = index, EffectType = FFBEType.ET_DMPR, Duration = ushort.MaxValue, Gain = 255, Polar = 1, Direction = 64 };
                    state.Running = true;
                    state.StartPending = true;
                    state.Iterations = -1;
                }
                state.Condition = c; break;
            case FfbEffRamp r:
                FfbDebugLogger.Log("PROTO-RAMP", $"Block={index} Start={(short)r.Start} End={(short)r.End}");
                if (state.Report == null && !_stoppedBlocks.Contains(index) && !_deviceReset)
                {
                    state.Report = new FfbEffReport { EffectBlockIndex = index, EffectType = FFBEType.ET_RAMP, Duration = ushort.MaxValue, Gain = 255, Polar = 1, Direction = 64 };
                    state.Running = true;
                    state.StartPending = true;
                    state.Iterations = -1;
                }
                state.Ramp = r; break;
            case FfbEffEnvelope e:
                state.Envelope = e; break;
            case FfbEffOp op:
                FfbDebugLogger.Log("PROTO-OP", $"Block={index} Op={op.EffectOp} Loops={op.LoopCount}");
                if (op.EffectOp == FFBOP.EFF_STOP)
                {
                    _stoppedBlocks.Add(index);
                    state.Running = false; state.StartPending = false; _sink.Stop(index); return;
                }
                if (!_enabled || _paused || op.EffectOp is not (FFBOP.EFF_START or FFBOP.EFF_SOLO)) return;
                if (op.EffectOp == FFBOP.EFF_SOLO) StopAll();
                _stoppedBlocks.Remove(index);
                _deviceReset = false;
                state.Running = true;
                state.StartPending = true;
                state.Iterations = (op.LoopCount is 0 or 255) ? -1 : op.LoopCount;
                break;
            default: return;
        }
        if (state.Running && state.Ready && _enabled && !_paused)
        {
            Apply(index, state);
        }
    }

    public void Refresh()
    {
        if (!_enabled || _paused) return;
        foreach (var (id, state) in _effects)
            if (state.Running && state.Ready)
            {
                Apply(id, state);
            }
    }

    public void StopAll()
    {
        foreach (var (id, state) in _effects)
        {
            _stoppedBlocks.Add(id);
            state.Running = false;
            state.StartPending = false;
            _sink.Stop(id);
        }
    }
}
