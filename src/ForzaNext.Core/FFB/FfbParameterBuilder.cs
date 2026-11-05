using SharpDX.DirectInput;
using ForzaNext.Core.Virtual;

namespace ForzaNext.Core.FFB;

public static class FfbParameterBuilder
{
    private static int ConstantMagnitude(int raw, FFBConfiguration config, double steering)
    {
        double value = raw * config.ConstantForceScale;
        if (value != 0) value = Math.CopySign(Math.Max(Math.Abs(value), config.MinimumForce * 10000), value);
        if (Math.Abs(value) > config.FilterThreshold * 10000)
            value *= 1 - Math.Abs(steering - 0.5) * 2 * (1 - config.MinimumCoefficient);
        return (int)Math.Clamp(Math.Round(value * (config.InvertConstantForce ? -1 : 1)), -10000, 10000);
    }

    public static EffectParameters Build(FfbEffectState state, FFBConfiguration config, double steering, int axisOffset, byte gain)
    {
        var r = state.Report ?? throw new InvalidOperationException("Effect report required.");
        if (!state.Ready) throw new InvalidOperationException("Effect parameters required.");
        var p = new EffectParameters
        {
            Flags = EffectFlags.ObjectOffsets | EffectFlags.Cartesian,
            Duration = r.Duration == ushort.MaxValue ? -1 : r.Duration * 1000,
            SamplePeriod = r.SamplePrd * 1000,
            StartDelay = r.StartDelay * 1000,
            Gain = (int)Math.Clamp(Math.Round(r.Gain / 255.0 * gain / 255.0 * config.MasterGain * 10000), 0, 10000),
            TriggerButton = -1, TriggerRepeatInterval = r.TrigerRpt * 1000
        };
        // This bridge drives one wheel actuator, not a two-axis joystick vector.
        // DirectInput's single-axis convention keeps direction zero and carries
        // polarity in the signed force payload. Projecting sin(angle) into gain
        // incorrectly silenced valid wheel effects at direction 0 (and 360).
        p.SetAxes(new[] { axisOffset }, new[] { 0 });
        if (state.Envelope is { } env)
            p.Envelope = new Envelope { AttackLevel = (int)Math.Min(env.AttackLevel, 10000u), FadeLevel = (int)Math.Min(env.FadeLevel, 10000u),
                AttackTime = (int)Math.Min((long)env.AttackTime * 1000, int.MaxValue), FadeTime = (int)Math.Min((long)env.FadeTime * 1000, int.MaxValue) };
        switch (r.EffectType)
        {
            case FFBEType.ET_CONST:
                int magnitude = ConstantMagnitude(state.Constant!.Value.Magnitude, config, steering);
                p.Parameters = new ConstantForce { Magnitude = magnitude };
                break;
            case FFBEType.ET_RAMP:
                p.Parameters = new RampForce { Start = ConstantMagnitude(state.Ramp!.Value.Start, config, steering), End = ConstantMagnitude(state.Ramp.Value.End, config, steering) };
                break;
            case FFBEType.ET_SPRNG:
            case FFBEType.ET_DMPR:
            case FFBEType.ET_INRT:
            case FFBEType.ET_FRCTN:
                var c = state.Condition!.Value;
                double coefficient = r.EffectType == FFBEType.ET_SPRNG ? config.SpringCoefficient : config.DamperCoefficient;
                double saturation = r.EffectType == FFBEType.ET_SPRNG ? config.SpringSaturation : config.DamperSaturation;
                p.Parameters = new ConditionSet { Conditions = new[] { new Condition
                {
                    PositiveCoefficient = (int)Math.Clamp(c.PosCoeff * coefficient, -10000, 10000),
                    NegativeCoefficient = (int)Math.Clamp(c.NegCoeff * coefficient, -10000, 10000),
                    PositiveSaturation = (int)Math.Clamp(c.PosSatur * saturation, 0, 10000),
                    NegativeSaturation = (int)Math.Clamp(c.NegSatur * saturation, 0, 10000),
                    Offset = Math.Clamp(c.CenterPointOffset, -10000, 10000), DeadBand = Math.Clamp(c.DeadBand, 0, 10000)
                } } };
                break;
            default:
                var periodic = state.Periodic!.Value;
                double mag = periodic.Magnitude * config.SineMagnitude;
                double frequency = config.SineFrequency;
                if (r.Gain == 32) { mag *= config.GearShiftStrength; frequency = config.GearShiftFrequency; }
                if (r.Gain == 1) { mag *= config.EngineVibrationStrength; frequency = config.EngineVibrationFrequency; }
                mag = Math.Clamp(mag, config.SineMinimumForce * 10000, config.SineMaximumForce * 10000);
                p.Parameters = new PeriodicForce { Magnitude = (int)mag, Offset = Math.Clamp(periodic.Offset, -10000, 10000),
                    Phase = (int)((periodic.Phase + config.SinePhase * 35999) % 36000),
                    Period = (int)Math.Clamp(periodic.Period * 1000.0 / Math.Max(0.01, frequency), 1, int.MaxValue) };
                break;
        }
        return p;
    }
}
