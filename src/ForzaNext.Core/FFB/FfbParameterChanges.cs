using SharpDX.DirectInput;

namespace ForzaNext.Core.FFB;

public static class FfbParameterChanges
{
    public static EffectParameterFlags Between(EffectParameters previous, EffectParameters next)
    {
        EffectParameterFlags flags = 0;
        if (previous.Duration != next.Duration) flags |= EffectParameterFlags.Duration;
        if (previous.SamplePeriod != next.SamplePeriod) flags |= EffectParameterFlags.SamplePeriod;
        if (previous.StartDelay != next.StartDelay) flags |= EffectParameterFlags.StartDelay;
        if (previous.Gain != next.Gain) flags |= EffectParameterFlags.Gain;
        if (previous.TriggerRepeatInterval != next.TriggerRepeatInterval) flags |= EffectParameterFlags.TriggerRepeatInterval;
        if (previous.TriggerButton != next.TriggerButton) flags |= EffectParameterFlags.TriggerButton;
        if (!previous.Directions.SequenceEqual(next.Directions)) flags |= EffectParameterFlags.Direction;
        if (!SameEnvelope(previous.Envelope, next.Envelope)) flags |= EffectParameterFlags.Envelope;
        bool same = (previous.Parameters, next.Parameters) switch
        {
            (ConstantForce a, ConstantForce b) => a.Magnitude == b.Magnitude,
            (RampForce a, RampForce b) => a.Start == b.Start && a.End == b.End,
            (PeriodicForce a, PeriodicForce b) => a.Magnitude == b.Magnitude && a.Offset == b.Offset && a.Phase == b.Phase && a.Period == b.Period,
            (ConditionSet a, ConditionSet b) => SameConditions(a.Conditions, b.Conditions),
            _ => false
        };
        if (!same) flags |= EffectParameterFlags.TypeSpecificParameters;
        return flags;
    }

    private static bool SameConditions(Condition[] a, Condition[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            var ca = a[i]; var cb = b[i];
            // Filter sub-1% coefficient noise to prevent flooding Direct Drive USB PID endpoints
            if (Math.Abs(ca.PositiveCoefficient - cb.PositiveCoefficient) > 50) return false;
            if (Math.Abs(ca.NegativeCoefficient - cb.NegativeCoefficient) > 50) return false;
            if (Math.Abs(ca.PositiveSaturation - cb.PositiveSaturation) > 100) return false;
            if (Math.Abs(ca.NegativeSaturation - cb.NegativeSaturation) > 100) return false;
            if (Math.Abs(ca.Offset - cb.Offset) > 100) return false;
            if (Math.Abs(ca.DeadBand - cb.DeadBand) > 100) return false;
        }
        return true;
    }
    private static bool SameEnvelope(Envelope? a, Envelope? b) => a == null ? b == null : b != null &&
        a.AttackLevel == b.AttackLevel && a.AttackTime == b.AttackTime && a.FadeLevel == b.FadeLevel && a.FadeTime == b.FadeTime;
}
