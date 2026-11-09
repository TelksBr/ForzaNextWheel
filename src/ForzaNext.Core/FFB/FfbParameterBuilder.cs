using SharpDX.DirectInput;
using ForzaNext.Core.Virtual;

namespace ForzaNext.Core.FFB;

public static class FfbParameterBuilder
{
    private static int CalculateEffectGain(FfbEffReport r, byte sessionGain, FFBConfiguration config)
    {
        double effectGain = r.Gain / 255.0;
        if (r.EffectType is FFBEType.ET_SINE or FFBEType.ET_SQR or FFBEType.ET_TRNGL or FFBEType.ET_STUP or FFBEType.ET_STDN)
        {
            // Perceptual boost for periodic textures (kerbs, engine RPM, road slip):
            // Lifts subtle vibrations (e.g. 1..15 out of 255) so they overcome motor inertia on direct drive rims,
            // while preserving smooth scaling up to full 100% force.
            if (effectGain > 0)
            {
                effectGain = Math.Pow(effectGain, 0.7);
            }
        }
        return (int)Math.Clamp(Math.Round(effectGain * (sessionGain / 255.0) * config.MasterGain * 10000), 0, 10000);
    }

    private static int ConstantMagnitude(int raw, FFBConfiguration config, double steering)
    {
        // HID PID Magnitude is a 16-bit signed integer (-10000 to +10000).
        // vJoy zero-extends 16-bit words into 32-bit fields, so negative values
        // arrive unsigned (e.g. 64857 for -679, 65535 for -1). Cast to short to restore sign.
        short signedRaw = (short)raw;
        double value = signedRaw * config.ConstantForceScale;
        if (value != 0) value = Math.CopySign(Math.Max(Math.Abs(value), config.MinimumForce * 10000), value);
        if (Math.Abs(value) > config.FilterThreshold * 10000)
        {
            double clampedSteering = Math.Clamp(steering, 0.0, 1.0);
            double lockFactor = Math.Abs(clampedSteering - 0.5) * 2.0; // 0.0 at center, 1.0 at full lock
            value *= 1.0 - lockFactor * (1.0 - Math.Clamp(config.MinimumCoefficient, 0.0, 1.0));
        }
        return (int)Math.Clamp(Math.Round(value * (config.InvertConstantForce ? -1 : 1)), -10000, 10000);
    }

    public static EffectParameters Build(FfbEffectState state, FFBConfiguration config, double steering, int axisOffset, byte gain)
    {
        var r = state.Report ?? throw new InvalidOperationException("Effect report required.");
        if (!state.Ready) throw new InvalidOperationException("Effect parameters required.");
        var p = new EffectParameters
        {
            Flags = EffectFlags.ObjectOffsets | EffectFlags.Cartesian,
            // In USB HID PID spec (sec 5.2.1), Duration 0 or 0xFFFF represents infinite duration (-1 in DirectInput)
            Duration = (r.Duration == ushort.MaxValue || r.Duration == 0) ? -1 : r.Duration * 1000,
            SamplePeriod = r.SamplePrd * 1000,
            StartDelay = r.StartDelay * 1000,
            Gain = CalculateEffectGain(r, gain, config),
            TriggerButton = -1, TriggerRepeatInterval = r.TrigerRpt * 1000
        };
        // This bridge drives one wheel actuator, not a two-axis joystick vector.
        // DirectInput's single-axis convention keeps direction zero and carries
        // polarity in the signed force payload. Projecting sin(angle) into gain
        // incorrectly silenced valid wheel effects at direction 0 (and 360).
        p.SetAxes(new[] { axisOffset }, new[] { 0 });
        if (state.Envelope is { } env)
            p.Envelope = new Envelope { AttackLevel = (int)Math.Min((ushort)env.AttackLevel, 10000u), FadeLevel = (int)Math.Min((ushort)env.FadeLevel, 10000u),
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
                short posCoeff = (short)c.PosCoeff;
                short negCoeff = (short)c.NegCoeff;
                short offset = (short)c.CenterPointOffset;
                ushort posSatur = (ushort)c.PosSatur;
                ushort negSatur = (ushort)c.NegSatur;
                ushort deadBand = (ushort)c.DeadBand;
                double coefficient = r.EffectType == FFBEType.ET_SPRNG ? config.SpringCoefficient : config.DamperCoefficient;
                double saturation = r.EffectType == FFBEType.ET_SPRNG ? config.SpringSaturation : config.DamperSaturation;
                p.Parameters = new ConditionSet { Conditions = new[] { new Condition
                {
                    PositiveCoefficient = (int)Math.Clamp(posCoeff * coefficient, -10000, 10000),
                    NegativeCoefficient = (int)Math.Clamp(negCoeff * coefficient, -10000, 10000),
                    PositiveSaturation = (int)Math.Clamp(posSatur * saturation, 0, 10000),
                    NegativeSaturation = (int)Math.Clamp(negSatur * saturation, 0, 10000),
                    Offset = Math.Clamp((int)offset, -10000, 10000), DeadBand = Math.Clamp((int)deadBand, 0, 10000)
                } } };
                break;
            default:
                var periodic = state.Periodic!.Value;
                ushort periodicMag = (ushort)periodic.Magnitude;
                short periodicOffset = (short)periodic.Offset;
                double mag = periodicMag * config.SineMagnitude;
                double frequency = config.SineFrequency <= 0 ? 1.0 : config.SineFrequency;
                if (r.Gain == 32) { mag *= config.GearShiftStrength; frequency = config.GearShiftFrequency; }
                else if (r.Gain == 1) { mag *= config.EngineVibrationStrength; frequency = config.EngineVibrationFrequency; }
                if (mag > 0)
                    mag = Math.Clamp(mag, config.SineMinimumForce * 10000, config.SineMaximumForce * 10000);
                else
                    mag = 0;
                int rawPhase = (int)Math.Round(periodic.Phase + config.SinePhase * 36000);
                int normalizedPhase = ((rawPhase % 36000) + 36000) % 36000;
                p.Parameters = new PeriodicForce { Magnitude = (int)mag, Offset = Math.Clamp((int)periodicOffset, -10000, 10000),
                    Phase = normalizedPhase,
                    Period = (int)Math.Clamp(periodic.Period * 1000.0 / Math.Max(0.01, frequency), 1, int.MaxValue) };
                break;
        }
        return p;
    }
}
