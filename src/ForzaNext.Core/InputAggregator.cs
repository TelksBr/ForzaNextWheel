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
        _profiles = profiles;
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
        var outPosition = new JoystickPositionV2();

        // Default all axes to center (vJoy range: 1 to 32768, center is 16384)
        outPosition.AxisX = 16384;
        outPosition.AxisY = 16384;
        outPosition.AxisZ = 16384;
        outPosition.AxisXRot = 16384;
        outPosition.AxisYRot = 16384;
        outPosition.AxisZRot = 16384;
        outPosition.Slider = 16384;
        outPosition.Dial = 16384;

        foreach (var profile in _profiles)
        {
            if (!_deviceStates.TryGetValue(profile.InstanceGuid, out var state))
                continue;

            // Map Axes
            if (profile.Axes != null)
            {
                foreach (var axis in profile.Axes)
                {
                    if (axis.AxisIndex < 0 || axis.AxisIndex >= state.Axes.Length)
                        continue;

                    int rawValue = state.Axes[axis.AxisIndex];
                    int processedValue = ProcessAxisValue(rawValue, axis.Inverted == 1, axis.Deadzone);

                    switch (axis.Id)
                    {
                        case "Steering":
                            outPosition.AxisX = processedValue;
                            break;
                        case "Throttle":
                            outPosition.AxisY = processedValue;
                            break;
                        case "Brake":
                            outPosition.AxisZ = processedValue;
                            break;
                        case "Clutch":
                            outPosition.AxisXRot = processedValue;
                            break;
                        case "Handbrake":
                            outPosition.AxisYRot = processedValue;
                            break;
                        case "Slider0":
                            outPosition.Slider = processedValue;
                            break;
                        case "Slider1":
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
                        }
                    }
                }
            }

            // Map DPad
            if (profile.DPad != null && state.PointOfView.Length > 0)
            {
                int povVal = state.PointOfView[0];
                if (povVal >= 0)
                {
                    outPosition.bHats = (uint)povVal;
                }
            }
        }

        return outPosition;
    }

    private static int ProcessAxisValue(int rawVal, bool inverted, double deadzone)
    {
        // DirectInput raw ranges are typically 0..65535.
        // vJoy expects 1..32768.
        double norm = rawVal / 65535.0; // 0.0 to 1.0

        if (inverted)
            norm = 1.0 - norm;

        // Apply deadzone from center
        if (deadzone > 0.0)
        {
            double center = 0.5;
            double diff = norm - center;
            if (Math.Abs(diff) < (deadzone / 2.0))
            {
                norm = center;
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

