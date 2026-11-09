using ForzaNext.Core.FFB;
using ForzaNext.Core.Virtual;
using SharpDX.DirectInput;
using Xunit;

namespace ForzaNext.Tests;

public class FfbParameterTests
{
    private static FfbEffectState Constant(ushort duration = 125) => new()
    {
        Report = new FfbEffReport { EffectType = FFBEType.ET_CONST, Duration = duration, Gain = 255, Polar = 1, Direction = 64 },
        Constant = new FfbEffConstant { Magnitude = 2000 }, Running = true
    };

    [Fact]
    public void DurationAxisAndGainsReachNativeParameters()
    {
        var p = FfbParameterBuilder.Build(Constant(), new() { MasterGain = 0.5, InvertConstantForce = false }, 0.5, 12, 128);
        Assert.Equal(125000, p.Duration);
        Assert.Equal(new[] { 12 }, p.Axes);
        Assert.InRange(p.Gain, 2500, 2510);
        Assert.Equal(2000, ((ConstantForce)p.Parameters).Magnitude);
    }

    [Fact]
    public void StartDelayIsPreservedInMicroseconds()
    {
        var state = Constant(); var report = state.Report!.Value; report.StartDelay = 75; state.Report = report;
        Assert.Equal(75000, FfbParameterBuilder.Build(state, new(), .5, 0, 255).StartDelay);
    }

    [Fact]
    public void MasterGainZeroSuppressesMinimumForce()
    {
        var p = FfbParameterBuilder.Build(Constant(), new() { MasterGain = 0, MinimumForce = 0.2 }, 0.5, 0, 255);
        Assert.Equal(0, p.Gain);
    }

    [Fact]
    public void InfiniteDurationIsMappedFromZeroAndMaxValue()
    {
        Assert.Equal(-1, FfbParameterBuilder.Build(Constant(ushort.MaxValue), new(), 0.5, 0, 255).Duration);
        Assert.Equal(-1, FfbParameterBuilder.Build(Constant(0), new(), 0.5, 0, 255).Duration);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(192)]
    [InlineData(255)]
    public void SingleAxisWheelDoesNotSilenceForceBasedOnVirtualPolarAngle(byte angle)
    {
        var state = Constant(); var report = state.Report!.Value; report.Direction = angle; state.Report = report;
        var parameters = FfbParameterBuilder.Build(state, new() { InvertConstantForce = false }, 0.5, 0, 255);
        Assert.Equal(10000, parameters.Gain);
        Assert.Equal(new[] { 0 }, parameters.Directions);
        Assert.Equal(2000, ((ConstantForce)parameters.Parameters).Magnitude);
    }

    [Theory]
    [InlineData(-2000, false, -2000)]
    [InlineData(-2000, true, 2000)]
    [InlineData(2000, false, 2000)]
    [InlineData(2000, true, -2000)]
    public void SingleAxisPolarityComesFromSignedMagnitudeAndConfiguredInversion(int raw, bool invert, int expected)
    {
        var state = Constant(); state.Constant = new FfbEffConstant { Magnitude = (short)raw };
        var parameters = FfbParameterBuilder.Build(state, new() { InvertConstantForce = invert }, 0.5, 0, 255);
        Assert.Equal(expected, ((ConstantForce)parameters.Parameters).Magnitude);
        Assert.Equal(new[] { 0 }, parameters.Directions);
    }

    [Theory]
    [InlineData(64857, true, 679)]    // 64857 is -679 in 16-bit signed; inverted -> +679
    [InlineData(64857, false, -679)]  // 64857 is -679 in 16-bit signed; non-inverted -> -679
    [InlineData(65535, true, 1)]      // 65535 is -1 in 16-bit signed; inverted -> +1
    [InlineData(65535, false, -1)]    // 65535 is -1 in 16-bit signed; non-inverted -> -1
    [InlineData(64465, true, 1071)]   // 64465 is -1071 in 16-bit signed; inverted -> +1071
    [InlineData(55536, true, 10000)]  // 55536 is -10000 in 16-bit signed; inverted -> +10000
    [InlineData(133, true, -133)]     // 133 is +133; inverted -> -133
    [InlineData(133, false, 133)]     // 133 is +133; non-inverted -> +133
    public void VJoyZeroExtendedNegativeMagnitudeIsCorrectlyInterpretedAsSigned(int raw, bool invert, int expected)
    {
        var state = Constant();
        state.Constant = new FfbEffConstant { Magnitude = raw };
        var parameters = FfbParameterBuilder.Build(state, new() { InvertConstantForce = invert }, 0.5, 0, 255);
        Assert.Equal(expected, ((ConstantForce)parameters.Parameters).Magnitude);
    }

    [Fact]
    public void UnsignedConditionCoefficientsAreInterpretedAsSigned()
    {
        var state = new FfbEffectState
        {
            Report = new FfbEffReport { EffectType = FFBEType.ET_SPRNG, Gain = 255, Duration = 100 },
            Condition = new FfbEffCond { PosCoeff = 65535, NegCoeff = 57536, PosSatur = 10000, NegSatur = 10000 }
        };
        var p = FfbParameterBuilder.Build(state, new() { SpringCoefficient = 1.0, MasterGain = 1.0 }, 0.5, 0, 255);
        var condition = ((ConditionSet)p.Parameters).Conditions.Single();
        Assert.Equal(-1, condition.PositiveCoefficient);
        Assert.Equal(-8000, condition.NegativeCoefficient);
    }

    [Fact]
    public void CartesianZeroDirectionDoesNotEraseSignedWheelTorque()
    {
        var state = Constant();
        var report = state.Report!.Value; report.Polar = 0; report.Direction = 0; state.Report = report;
        state.Constant = new FfbEffConstant { Magnitude = -1500 };
        var parameters = FfbParameterBuilder.Build(state, new() { InvertConstantForce = false, MasterGain = 0.5 }, 0.5, 0, 255);
        Assert.Equal(5000, parameters.Gain);
        Assert.Equal(-1500, ((ConstantForce)parameters.Parameters).Magnitude);
    }

    [Fact]
    public void ConstantInversionAndFalloffAreAppliedOnce()
    {
        var p = FfbParameterBuilder.Build(Constant(), new() { InvertConstantForce = true, MinimumCoefficient = 0.25, FilterThreshold = 0.1 }, 1, 0, 255);
        Assert.Equal(-500, ((ConstantForce)p.Parameters).Magnitude);
    }

    [Fact]
    public void NegativeConditionCoefficientsArePreservedAndMasterGainApplies()
    {
        var state = new FfbEffectState
        {
            Report = new FfbEffReport { EffectType = FFBEType.ET_DMPR, Gain = 255, Duration = 100 },
            Condition = new FfbEffCond { PosCoeff = -8000, NegCoeff = 4000, PosSatur = 10000, NegSatur = 10000 }
        };
        var p = FfbParameterBuilder.Build(state, new() { DamperCoefficient = 0.5, MasterGain = 0.5 }, 0.5, 0, 255);
        var condition = ((ConditionSet)p.Parameters).Conditions.Single();
        Assert.Equal(-4000, condition.PositiveCoefficient);
        Assert.Equal(2000, condition.NegativeCoefficient);
        Assert.Equal(5000, p.Gain);
    }

    [Fact]
    public void PeriodEnvelopeAndPhaseAreConvertedWithBounds()
    {
        var state = new FfbEffectState
        {
            Report = new FfbEffReport { EffectType = FFBEType.ET_SINE, Gain = 255, Duration = 1000, Polar = 1, Direction = 64 },
            Periodic = new FfbEffPeriod { Magnitude = 9000, Period = 25, Phase = 18000, Offset = -300 },
            Envelope = new FfbEffEnvelope { AttackTime = 50, FadeTime = uint.MaxValue, AttackLevel = 2000, FadeLevel = 15000 }
        };
        var p = FfbParameterBuilder.Build(state, new() { SineFrequency = 0.5, SinePhase = 0, SineMaximumForce = 0.4 }, 0.5, 0, 255);
        var sine = (PeriodicForce)p.Parameters;
        Assert.Equal(50000, sine.Period); Assert.Equal(4000, sine.Magnitude);
        Assert.Equal(18000, sine.Phase); Assert.Equal(-300, sine.Offset);
        Assert.Equal(50000, p.Envelope.AttackTime); Assert.Equal(int.MaxValue, p.Envelope.FadeTime);
        Assert.Equal(10000, p.Envelope.FadeLevel);
    }

    [Fact]
    public void IncompleteEffectCannotBuildNativeParameters()
        => Assert.Throws<InvalidOperationException>(() => FfbParameterBuilder.Build(new(), new(), 0.5, 0, 255));

    [Fact]
    public void PeriodicZeroMagnitudeYieldsZeroEvenWithMinimumForce()
    {
        var state = new FfbEffectState
        {
            Report = new FfbEffReport { EffectType = FFBEType.ET_SINE, Gain = 255, Duration = 1000 },
            Periodic = new FfbEffPeriod { Magnitude = 0, Period = 50 }
        };
        var p = FfbParameterBuilder.Build(state, new() { SineMinimumForce = 0.05, SineMaximumForce = 0.5 }, 0.5, 0, 255);
        var sine = (PeriodicForce)p.Parameters;
        Assert.Equal(0, sine.Magnitude);
    }

    [Theory]
    [InlineData(0, -0.25, 27000)]
    [InlineData(0, -0.5, 18000)]
    [InlineData(1000, -0.1, 33400)]
    [InlineData(0, 0.5, 18000)]
    public void PeriodicPhaseWrappingIsAlwaysNonNegative(int initialPhase, double sinePhaseConfig, int expectedPhase)
    {
        var state = new FfbEffectState
        {
            Report = new FfbEffReport { EffectType = FFBEType.ET_SINE, Gain = 255, Duration = 1000 },
            Periodic = new FfbEffPeriod { Magnitude = 1000, Period = 50, Phase = (uint)initialPhase }
        };
        var p = FfbParameterBuilder.Build(state, new() { SinePhase = sinePhaseConfig }, 0.5, 0, 255);
        var sine = (PeriodicForce)p.Parameters;
        Assert.InRange(sine.Phase, 0, 35999);
        Assert.Equal(expectedPhase, sine.Phase);
    }

    [Theory]
    [InlineData(1.0, 1000, 0.1)]
    [InlineData(1.1, 1000, 0.1)]
    [InlineData(-0.1, 1000, 0.1)]
    [InlineData(1.5, 1000, 0.1)]
    public void SteeringOvershootDoesNotInvertForce(double steering, int rawMag, double minCoeff)
    {
        var state = Constant();
        state.Constant = new FfbEffConstant { Magnitude = rawMag };
        var p = FfbParameterBuilder.Build(state, new() { InvertConstantForce = false, MinimumCoefficient = minCoeff, FilterThreshold = 0.01 }, steering, 0, 255);
        var cf = (ConstantForce)p.Parameters;
        Assert.True(cf.Magnitude > 0, $"Magnitude was inverted: {cf.Magnitude} for steering {steering}");
        Assert.InRange(cf.Magnitude, 90, 1000);
    }

    [Fact]
    public void ShiftAndEngineGainReportsScaleStrengthAndFrequencyStandardized()
    {
        var shiftState = new FfbEffectState
        {
            Report = new FfbEffReport { EffectType = FFBEType.ET_SINE, Gain = 32, Duration = 250 },
            Periodic = new FfbEffPeriod { Magnitude = 10000, Period = 50 }
        };
        var engineState = new FfbEffectState
        {
            Report = new FfbEffReport { EffectType = FFBEType.ET_SINE, Gain = 1, Duration = 350 },
            Periodic = new FfbEffPeriod { Magnitude = 10000, Period = 50 }
        };
        var config = new FFBConfiguration
        {
            GearShiftStrength = 0.8,
            GearShiftFrequency = 2.0,
            EngineVibrationStrength = 0.2,
            EngineVibrationFrequency = 0.5,
            SineMaximumForce = 1.0,
            SineMinimumForce = 0.0
        };

        var pShift = FfbParameterBuilder.Build(shiftState, config, 0.5, 0, 255);
        var sineShift = (PeriodicForce)pShift.Parameters;
        Assert.Equal(8000, sineShift.Magnitude);
        Assert.Equal(25000, sineShift.Period);

        var pEngine = FfbParameterBuilder.Build(engineState, config, 0.5, 0, 255);
        var sineEngine = (PeriodicForce)pEngine.Parameters;
        Assert.Equal(2000, sineEngine.Magnitude);
        Assert.Equal(100000, sineEngine.Period);
    }
}
