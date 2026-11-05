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
    public void InfiniteAndZeroDurationRemainDistinct()
    {
        Assert.Equal(-1, FfbParameterBuilder.Build(Constant(ushort.MaxValue), new(), 0.5, 0, 255).Duration);
        Assert.Equal(0, FfbParameterBuilder.Build(Constant(0), new(), 0.5, 0, 255).Duration);
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
}
