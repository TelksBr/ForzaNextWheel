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
            (ConditionSet a, ConditionSet b) => a.Conditions.SequenceEqual(b.Conditions),
            _ => false
        };
        if (!same) flags |= EffectParameterFlags.TypeSpecificParameters;
        return flags;
    }
    private static bool SameEnvelope(Envelope? a, Envelope? b) => a == null ? b == null : b != null &&
        a.AttackLevel == b.AttackLevel && a.AttackTime == b.AttackTime && a.FadeLevel == b.FadeLevel && a.FadeTime == b.FadeTime;
}
