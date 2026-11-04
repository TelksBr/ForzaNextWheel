using System;
using System.Linq;
using Vortice.DirectInput;

namespace ForzaNext.Core.FFB;

public class FFBConfiguration
{
    public double MasterGain { get; set; } = 1.0;         // 0.0 to 1.0
    public double ConstantForceScale { get; set; } = 1.0; // Multiplier
    public double DamperCoefficient { get; set; } = 0.15; // Inherent wheel resistance
    public double SpringCoefficient { get; set; } = 0.15; // Centering spring
    public double FrictionCoefficient { get; set; } = 0.05;
    public double FilterThreshold { get; set; } = 0.05;   // Filter out micro-jitter
}

public class DirectInputFFBBridge : IDisposable
{
    private readonly IDirectInputDevice8? _device;
    private readonly int[] _axes = new int[1];
    private readonly int[] _directions = new int[1];

    private IDirectInputEffect? _constantEffect;
    private IDirectInputEffect? _damperEffect;
    private IDirectInputEffect? _springEffect;

    private readonly ConstantForce _constantForceData = new();
    private readonly ConditionSet _damperData = new();
    private readonly ConditionSet _springData = new();

    private readonly EffectParameters _constantParams = new();
    private readonly EffectParameters _damperParams = new();
    private readonly EffectParameters _springParams = new();

    private double _lastTargetForce = 0;

    public bool IsInitialized { get; private set; }
    public FFBConfiguration Config { get; } = new();

    public DirectInputFFBBridge(IDirectInputDevice8 device)
    {
        _device = device;
    }

    public bool Initialize()
    {
        if (_device == null) return false;

        try
        {
            // Find FFB actuator objects (usually Axis X for steering)
            var objects = _device.GetObjects(DeviceObjectTypeFlags.ForceFeedbackActuator).ToList();
            if (!objects.Any())
            {
                Console.WriteLine("[FFB] No Force Feedback actuators reported on this device.");
                return false;
            }

            _axes[0] = (int)objects[0].ObjectId.Flags;
            _directions[0] = 0;

            // Turn off hardware autocentering so the game or our spring controls it
            try
            {
                _device.Properties.AutoCenter = false;
            }
            catch { }

            // 1. Initialize Constant Force Effect (Rack Force)
            _constantParams.Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets;
            _constantParams.Duration = -1; // Infinite
            _constantParams.SamplePeriod = 0;
            _constantParams.Gain = 10000;
            _constantParams.TriggerButton = -1;
            _constantParams.TriggerRepeatInterval = 0;
            _constantParams.SetAxes(_axes, _directions);
            _constantParams.Parameters = _constantForceData;

            try
            {
                _constantEffect = _device.CreateEffect(EffectGuid.ConstantForce, _constantParams);
                _constantEffect.Start(1, EffectPlayFlags.NoDownload);
                Console.WriteLine("[FFB] Constant Force effect created successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FFB WARNING] Constant Force init: {ex.Message}");
            }

            // 2. Initialize Damper Effect (Wheel Weight)
            _damperParams.Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets;
            _damperParams.Duration = -1;
            _damperParams.Gain = 10000;
            _damperParams.TriggerButton = -1;
            _damperParams.SetAxes(_axes, _directions);
            _damperParams.Parameters = _damperData;

            try
            {
                _damperEffect = _device.CreateEffect(EffectGuid.Damper, _damperParams);
                _damperEffect.Start(1, EffectPlayFlags.NoDownload);
            }
            catch { }

            // 3. Initialize Spring Effect (Centering Spring)
            _springParams.Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets;
            _springParams.Duration = -1;
            _springParams.Gain = 10000;
            _springParams.TriggerButton = -1;
            _springParams.SetAxes(_axes, _directions);
            _springParams.Parameters = _springData;

            try
            {
                _springEffect = _device.CreateEffect(EffectGuid.Spring, _springParams);
                _springEffect.Start(1, EffectPlayFlags.NoDownload);
            }
            catch { }

            IsInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FFB ERROR] Failed to initialize FFB Bridge: {ex.Message}");
            return false;
        }
    }

    public void ApplyForce(double normalizedForce)
    {
        if (!IsInitialized || _constantEffect == null) return;

        // Apply Deadband / Threshold to eliminate high-frequency motor whine
        if (Math.Abs(normalizedForce - _lastTargetForce) < Config.FilterThreshold && Math.Abs(normalizedForce) < Config.FilterThreshold)
        {
            normalizedForce = 0;
        }

        _lastTargetForce = normalizedForce;

        // Scale force: -10000 to +10000 DI range
        double finalForce = normalizedForce * Config.MasterGain * Config.ConstantForceScale;
        int diForce = (int)Math.Clamp(Math.Round(finalForce * 10000.0), -10000, 10000);

        _constantForceData.Magnitude = diForce;
        _constantParams.Parameters = _constantForceData;

        try
        {
            _constantEffect.SetParameters(_constantParams, EffectParameterFlags.TypeSpecificParameters);
        }
        catch { }
    }

    public void SetDamper(double coefficient)
    {
        if (!IsInitialized || _damperEffect == null) return;
        Config.DamperCoefficient = coefficient;

        int diCoeff = (int)Math.Clamp(Math.Round(coefficient * 10000.0), 0, 10000);
        _damperData.Conditions = new[] {
            new Condition {
                NegativeCoefficient = diCoeff,
                PositiveCoefficient = diCoeff,
                NegativeSaturation = 10000,
                PositiveSaturation = 10000
            }
        };
        _damperParams.Parameters = _damperData;

        try
        {
            _damperEffect.SetParameters(_damperParams, EffectParameterFlags.TypeSpecificParameters);
        }
        catch { }
    }

    public void StopAll()
    {
        try { _constantEffect?.Stop(); } catch { }
        try { _damperEffect?.Stop(); } catch { }
        try { _springEffect?.Stop(); } catch { }
    }

    public void Dispose()
    {
        StopAll();
        _constantEffect?.Dispose();
        _damperEffect?.Dispose();
        _springEffect?.Dispose();
        IsInitialized = false;
    }
}
