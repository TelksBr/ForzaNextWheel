using SharpDX.DirectInput;
using ForzaNext.Core.Virtual;
using ForzaNext.Core.Config;

namespace ForzaNext.Core.FFB;

public class FFBConfiguration
{
    public double SineMaximumForce { get; set; } = 0.5;
    public double SineMinimumForce { get; set; }
    public double SinePhase { get; set; } = 0.375;

    // Constant Force (Cremalheira)
    public double MasterGain { get; set; } = 1.0;          // 0.0 to 1.0 (0% a 100%)
    public double ConstantForceScale { get; set; } = 1.0;  // Multiplicador (0.5x a 2.5x)
    public double MinimumForce { get; set; } = 0.0;        // 0.0 to 0.20 (0% a 20%)
    public double FilterThreshold { get; set; } = 0.10;    // Início de atenuação (0.01 a 0.5)
    public double MinimumCoefficient { get; set; } = 1.0;  // Coeficiente no batente (0.1 a 1.0)
    public bool InvertConstantForce { get; set; } = true;  // Inverter direção do rack

    // Condições Físicas (Chassi & Direct Drive)
    public double DamperCoefficient { get; set; } = 0.15;  // Peso / Amortecimento (0.0 a 1.0)
    public double DamperSaturation { get; set; } = 1.0;    // Saturação do Damper
    public double SpringCoefficient { get; set; } = 0.15;  // Mola de Centralização (0.0 a 1.0)
    public double SpringSaturation { get; set; } = 1.0;    // Saturação da Mola

    // Vibrações Periódicas (Sine / Efeitos do Forza)
    public double SineMagnitude { get; set; } = 1.0;
    public double SineFrequency { get; set; } = 0.375;
    public double GearShiftStrength { get; set; } = 0.30;     // Impacto de troca de marcha (0.0 a 1.0)
    public double GearShiftFrequency { get; set; } = 1.0;
    public double EngineVibrationStrength { get; set; } = 0.15; // Vibração de RPM/Motor (0.0 a 1.0)
    public double EngineVibrationFrequency { get; set; } = 0.30;
}

// All methods are called by the session worker, never from the native callback or UI.
public sealed class DirectInputFFBBridge : IFfbEffectSink, IDisposable
{
    private readonly Joystick _joystick;
    private readonly Dictionary<byte, Effect> _effects = new();
    private readonly Dictionary<byte, EffectParameters> _parameters = new();
    private readonly HashSet<Guid> _supported;
    private int _axisOffset;
    private readonly HashSet<FFBEType> _unsupported = new();
    public string? Warning { get; private set; }
    public FFBConfiguration Config { get; set; } = new();
    public double NormalizedSteering { get; set; } = 0.5;
    public bool IsInitialized { get; private set; }
    private readonly Dictionary<byte, int> _magnitudes = new();
    private readonly Dictionary<byte, long> _deadlines = new();
    private readonly Dictionary<byte, FFBEType> _effectTypes = new();
    private bool _actuatorsEnabled = true, _paused;
    private bool IsPlaying(byte index) => _actuatorsEnabled && !_paused && _deadlines.TryGetValue(index, out long deadline) && Environment.TickCount64 < deadline;
    public int ActiveEffectCount => _deadlines.Keys.Count(IsPlaying);
    public string ActiveEffectTypes => string.Join(", ", _effectTypes.Where(p => IsPlaying(p.Key)).Select(p => p.Value.ToString().Replace("ET_", "")).Distinct());
    private long ConstantMagnitude
    {
        get { long total = 0; foreach (var pair in _magnitudes) if (IsPlaying(pair.Key)) total += pair.Value; return total; }
    }
    public bool IsSaturated => Math.Abs(ConstantMagnitude) >= 10000;
    public int CurrentMagnitude
    {
        get
        {
            return (int)Math.Clamp(ConstantMagnitude, -10000, 10000);
        }
    }
    public DirectInputFFBBridge(Joystick joystick)
    {
        _joystick = joystick;
        _supported = joystick.GetEffects().Select(e => e.Guid).ToHashSet();
    }

    public bool Initialize()
    {
        var actuator = _joystick.GetObjects().FirstOrDefault(o => (o.ObjectId.Flags & DeviceObjectTypeFlags.ForceFeedbackActuator) != 0);
        if (actuator == null || !_supported.Contains(EffectGuid.ConstantForce)) return false;
        _axisOffset = actuator.Offset;
        _joystick.SendForceFeedbackCommand(ForceFeedbackCommand.Reset);
        _joystick.SendForceFeedbackCommand(ForceFeedbackCommand.SetActuatorsOn);
        // Probe synchronously on the owning worker. No abandoned timeout threads.
        var report = new FfbEffReport { EffectBlockIndex = 255, EffectType = FFBEType.ET_CONST, Duration = 250, Gain = 255, Polar = 1, Direction = 64 };
        Apply(255, new FfbEffectState { Report = report, Constant = new FfbEffConstant(), Iterations = 1 }, 255, false);
        Free(255);
        IsInitialized = true;
        return true;
    }

    public static FFBConfiguration FromProfile(FFBParameters? f) => new()
    {
        MasterGain = f?.Const?.MaximumForce ?? 1,
        ConstantForceScale = f?.Const?.Magnitude ?? 1,
        MinimumForce = f?.Const?.MinimumForce ?? 0,
        FilterThreshold = f?.Const?.FilterThreshold ?? 0.1,
        MinimumCoefficient = f?.Const?.MinimumCoefficient ?? 1,
        InvertConstantForce = f?.InvertConstantForce ?? true,
        DamperCoefficient = f?.Damper?.Coefficient ?? 0.15,
        DamperSaturation = f?.Damper?.Saturation ?? 1,
        SpringCoefficient = f?.Spring?.Coefficient ?? 0.15,
        SpringSaturation = f?.Spring?.Saturation ?? 1,
        SineMagnitude = f?.Sine?.Magnitude ?? 1,
        SineFrequency = f?.Sine?.Frequency ?? 0.375,
        SineMaximumForce = f?.Sine?.MaximumForce ?? 0.5,
        SineMinimumForce = f?.Sine?.MinimumForce ?? 0,
        SinePhase = f?.Sine?.Phase ?? 0.375,
        GearShiftStrength = f?.Sine?.GearShiftVibrations?.Strength ?? 0.3,
        GearShiftFrequency = f?.Sine?.GearShiftVibrations?.Frequency ?? 1,
        EngineVibrationStrength = f?.Sine?.EngineVibrations?.Strength ?? 0.15,
        EngineVibrationFrequency = f?.Sine?.EngineVibrations?.Frequency ?? 0.3
    };

    private static Guid EffectType(FFBEType type) => type switch
    {
        FFBEType.ET_CONST => EffectGuid.ConstantForce, FFBEType.ET_RAMP => EffectGuid.RampForce,
        FFBEType.ET_SINE => EffectGuid.Sine, FFBEType.ET_SQR => EffectGuid.Square,
        FFBEType.ET_TRNGL => EffectGuid.Triangle, FFBEType.ET_STUP => EffectGuid.SawtoothUp,
        FFBEType.ET_STDN => EffectGuid.SawtoothDown, FFBEType.ET_SPRNG => EffectGuid.Spring,
        FFBEType.ET_DMPR => EffectGuid.Damper, FFBEType.ET_INRT => EffectGuid.Inertia,
        FFBEType.ET_FRCTN => EffectGuid.Friction,
        _ => throw new NotSupportedException($"Efeito não suportado: {type}")
    };

    public void Apply(byte index, FfbEffectState state, byte gain, bool start)
    {
        if (state.Report is not { } r || !state.Ready) return;
        var guid = EffectType(r.EffectType);
        _effectTypes[index] = r.EffectType;
        if (!_supported.Contains(guid))
        {
            if (_unsupported.Add(r.EffectType))
            {
                Warning = "Efeitos não suportados pelo volante: " + string.Join(", ", _unsupported);
                DiagnosticLog.Write("Partial FFB", Warning);
            }
            return;
        }
        var p = FfbParameterBuilder.Build(state, Config, NormalizedSteering, _axisOffset, gain);
        if (!_effects.TryGetValue(index, out var effect))
        {
            effect = new Effect(_joystick, guid, p);
            _effects.Add(index, effect);
            if (state.Running) start = true;
        }
        else
        {
            var flags = FfbParameterChanges.Between(_parameters[index], p);
            if (flags != 0) effect.SetParameters(p, flags | EffectParameterFlags.NoRestart);
        }
        _parameters[index] = p;
        if (p.Parameters is ConstantForce constant)
            _magnitudes[index] = (int)(constant.Magnitude * p.Gain / 10000.0);
        if (start)
        {
            effect.Start(state.Iterations, EffectPlayFlags.None);
            _deadlines[index] = r.Duration == ushort.MaxValue || state.Iterations < 0
                ? long.MaxValue : Environment.TickCount64 + (long)r.Duration * state.Iterations;
        }
    }

    public void Stop(byte index)
    {
        if (_effects.TryGetValue(index, out var effect)) effect.Stop();
        _deadlines.Remove(index);
    }
    public void Free(byte index)
    {
        _parameters.Remove(index);
        _deadlines.Remove(index);
        _magnitudes.Remove(index);
        _effectTypes.Remove(index);
        if (_effects.Remove(index, out var effect))
        {
            try { effect.Stop(); } finally { effect.Dispose(); }
        }
    }
    public void Control(FFB_CTRL control)
    {
        if (control == FFB_CTRL.CTRL_DISACT) _actuatorsEnabled = false;
        if (control == FFB_CTRL.CTRL_ENACT) _actuatorsEnabled = true;
        if (control == FFB_CTRL.CTRL_DEVPAUSE) _paused = true;
        if (control == FFB_CTRL.CTRL_DEVCONT) _paused = false;
        if (control == FFB_CTRL.CTRL_DEVRST) { _paused = false; _actuatorsEnabled = true; }
        var command = control switch
        {
            FFB_CTRL.CTRL_ENACT => ForceFeedbackCommand.SetActuatorsOn,
            FFB_CTRL.CTRL_DISACT => ForceFeedbackCommand.SetActuatorsOff,
            FFB_CTRL.CTRL_DEVPAUSE => ForceFeedbackCommand.Pause,
            FFB_CTRL.CTRL_DEVCONT => ForceFeedbackCommand.Continue,
            FFB_CTRL.CTRL_DEVRST => ForceFeedbackCommand.Reset,
            _ => ForceFeedbackCommand.StopAll
        };
        _joystick.SendForceFeedbackCommand(command);
    }
    public void StopAll()
    {
        foreach (var id in _effects.Keys.ToArray())
            try { Stop(id); } catch (Exception ex) { DiagnosticLog.Write("Stop effect", ex.ToString()); }
        try { _joystick.SendForceFeedbackCommand(ForceFeedbackCommand.StopAll); }
        catch (Exception ex) { DiagnosticLog.Write("Stop all", ex.ToString()); }
    }
    public void Dispose()
    {
        StopAll();
        try { _joystick.SendForceFeedbackCommand(ForceFeedbackCommand.SetActuatorsOff); }
        catch (Exception ex) { DiagnosticLog.Write("Disable actuators", ex.ToString()); }
        foreach (var id in _effects.Keys.ToArray())
            try { Free(id); } catch (Exception ex) { DiagnosticLog.Write("Free effect", ex.ToString()); }
        IsInitialized = false;
    }
}
