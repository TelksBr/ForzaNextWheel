using System;
using System.Collections.Generic;
using ForzaNext.Core.Config;
using ForzaNext.Core.Devices;
using ForzaNext.Core.Virtual;

namespace ForzaNext.Core;

public class InputAggregator
{
    private readonly List<ControllerProfile> _profiles;
    private readonly Dictionary<Guid, PolledInputState> _deviceStates = new();

    public InputAggregator(List<ControllerProfile> profiles)
    {
        _profiles = ProfileManager.Snapshot(profiles);
        foreach (var p in profiles)
        {
            _deviceStates[p.InstanceGuid] = new PolledInputState();
        }
    }

    public PolledInputState GetStateBuffer(Guid instanceGuid)
    {
        if (!_deviceStates.TryGetValue(instanceGuid, out var state))
        {
            state = new PolledInputState();
            _deviceStates[instanceGuid] = state;
        }
        return state;
    }

    public JoystickPositionV2 Aggregate()
    {
        var outPosition = NeutralPosition();

        bool dpadUp = false, dpadDown = false, dpadLeft = false, dpadRight = false;
        bool physicalPovFound = false;
        bool digitalHandbrake = false;

        foreach (var profile in _profiles)
        {
            if (!_deviceStates.TryGetValue(profile.InstanceGuid, out var state) || !state.IsValid)
                continue;

            // Map Axes
            // Matching standard Forza franchise vJoy bindings:
            // Steering  -> AxisX
            // Throttle  -> AxisZ
            // Brake     -> AxisXRot
            // Clutch    -> AxisYRot
            // Handbrake -> AxisZRot
            if (profile.Axes != null)
            {
                foreach (var axis in profile.Axes)
                {
                    if (axis.AxisIndex < 0 || axis.AxisIndex >= state.Axes.Length)
                        continue;

                    int rawValue = state.Axes[axis.AxisIndex];
                    bool isSteering = axis.Id.Equals("Steering", StringComparison.OrdinalIgnoreCase);
                    int processedValue = ProcessAxisValue(rawValue, axis.Inverted == 1, axis.Deadzone, isSteering, axis.Calibration);

                    switch (axis.Id.ToLowerInvariant())
                    {
                        case "steering":
                            outPosition.AxisX = processedValue;
                            break;
                        case "throttle":
                            outPosition.AxisZ = processedValue;
                            break;
                        case "brake":
                            outPosition.AxisXRot = processedValue;
                            break;
                        case "clutch":
                            outPosition.AxisYRot = processedValue;
                            break;
                        case "handbrake":
                            outPosition.AxisZRot = processedValue;
                            break;
                        case "slider0":
                            outPosition.Slider = processedValue;
                            break;
                        case "slider1":
                            outPosition.Dial = processedValue;
                            break;
                    }
                }
            }

            // Map Buttons
            if (profile.Buttons != null)
            {
                foreach (var btn in profile.Buttons)
                {
                    if (btn.Index >= 0 && btn.Index < state.Buttons.Length)
                    {
                        bool isPressed = state.Buttons[btn.Index];
                        if (isPressed)
                        {
                            SetButtonBit(ref outPosition, btn.Id);

                            // Dedicated digital handbrake (Id 26 / Button 27); RB remains independent.
                            // Pulls virtual Handbrake axis to 100% (32768) so game activates it via button OR axis!
                            if (btn.Id == 26)
                            {
                                digitalHandbrake = true;
                            }

                            // Directional D-Pad Buttons
                            if (btn.Id == 32) dpadUp = true;
                            else if (btn.Id == 33) dpadDown = true;
                            else if (btn.Id == 34) dpadLeft = true;
                            else if (btn.Id == 35) dpadRight = true;
                        }
                    }
                }
            }

            // Map physical DPad / POV if available
            if (profile.DPad != null && profile.DPad.Index >= 0 && profile.DPad.Index < state.PointOfView.Length)
            {
                int povVal = state.PointOfView[profile.DPad.Index];
                if (povVal is >= 0 and < 36000)
                {
                    outPosition.bHats = (uint)povVal;
                    physicalPovFound = true;
                }
            }
        }

        // Synthesize D-Pad POV if directional buttons pressed and no physical POV override
        if (!physicalPovFound)
        {
            if (dpadUp && dpadDown) dpadUp = dpadDown = false;
            if (dpadLeft && dpadRight) dpadLeft = dpadRight = false;
            if (dpadUp && dpadRight) outPosition.bHats = 4500;
            else if (dpadDown && dpadRight) outPosition.bHats = 13500;
            else if (dpadDown && dpadLeft) outPosition.bHats = 22500;
            else if (dpadUp && dpadLeft) outPosition.bHats = 31500;
            else if (dpadUp) outPosition.bHats = 0;
            else if (dpadRight) outPosition.bHats = 9000;
            else if (dpadDown) outPosition.bHats = 18000;
            else if (dpadLeft) outPosition.bHats = 27000;
        }

        if (digitalHandbrake) outPosition.AxisZRot = 32768;
        return outPosition;
    }

    public static JoystickPositionV2 NeutralPosition() => new()
    {
        AxisX = 16384, AxisY = 16384, AxisZ = 1, AxisXRot = 1,
        AxisYRot = 1, AxisZRot = 1, Slider = 16384, Dial = 16384,
        bHats = uint.MaxValue, bHatsEx1 = uint.MaxValue,
        bHatsEx2 = uint.MaxValue, bHatsEx3 = uint.MaxValue
    };

    public static int ProcessAxisValue(int rawVal, bool inverted, double deadzone, bool isSteering, AxisCalibration? calibration = null)
    {
        // DirectInput raw ranges are typically 0..65535.
        // vJoy expects 1..32768.
        deadzone = double.IsFinite(deadzone) ? Math.Clamp(deadzone, 0, 0.99) : 0;
        double norm = calibration?.Normalize(rawVal, isSteering) ?? Math.Clamp(rawVal, 0, 65535) / 65535.0;

        if (inverted)
            norm = 1.0 - norm;

        if (deadzone > 0.0)
        {
            if (isSteering)
            {
                // Steering deadzone around center with smooth linear ramp to prevent step notch
                double center = 0.5;
                double diff = norm - center;
                double halfDead = deadzone / 2.0;
                if (Math.Abs(diff) <= halfDead)
                {
                    norm = center;
                }
                else
                {
                    double sign = Math.Sign(diff);
                    norm = center + sign * ((Math.Abs(diff) - halfDead) / (0.5 - halfDead)) * 0.5;
                }
            }
            else
            {
                // Pedal deadzone from rest (bottom)
                if (norm < deadzone)
                {
                    norm = 0.0;
                }
                else
                {
                    norm = (norm - deadzone) / (1.0 - deadzone);
                }
            }
        }

        int vjoyVal = (int)Math.Clamp(Math.Round(norm * 32767.0) + 1, 1, 32768);
        return vjoyVal;
    }

    private static void SetButtonBit(ref JoystickPositionV2 pos, int buttonId)
    {
        if (buttonId < 0 || buttonId >= 128) return;

        if (buttonId < 32)
            pos.Buttons |= (1u << buttonId);
        else if (buttonId < 64)
            pos.ButtonsEx1 |= (1u << (buttonId - 32));
        else if (buttonId < 96)
            pos.ButtonsEx2 |= (1u << (buttonId - 64));
        else
            pos.ButtonsEx3 |= (1u << (buttonId - 96));
    }
}
