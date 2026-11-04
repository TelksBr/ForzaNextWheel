using System;
using System.Collections.Generic;
using System.Linq;
using Vortice.DirectInput;

namespace ForzaNext.Core.Devices;

public class ConnectedDeviceInfo
{
    public Guid InstanceGuid { get; set; }
    public Guid ProductGuid { get; set; }
    public string InstanceName { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public DeviceType DeviceType { get; set; }
    public bool HasFFB { get; set; }
}

public class PolledInputState
{
    public int[] Axes { get; set; } = new int[8]; // X, Y, Z, Rx, Ry, Rz, Slider0, Slider1
    public bool[] Buttons { get; set; } = new bool[128];
    public int[] PointOfView { get; set; } = new int[4];
}

public class DirectInputDeviceManager : IDisposable
{
    private readonly IDirectInput8 _directInput;
    private readonly Dictionary<Guid, IDirectInputDevice8> _acquiredDevices = new();

    public DirectInputDeviceManager()
    {
        _directInput = DInput.DirectInput8Create();
    }

    public List<ConnectedDeviceInfo> EnumerateDevices()
    {
        var result = new List<ConnectedDeviceInfo>();
        var devices = _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AllDevices);

        foreach (var dev in devices)
        {
            bool hasFfb = false;
            try
            {
                using var tempJoy = _directInput.CreateDevice(dev.InstanceGuid);
                var objects = tempJoy.GetObjects(DeviceObjectTypeFlags.ForceFeedbackActuator);
                hasFfb = objects.Any();
            }
            catch
            {
                // Ignore device probing failure
            }

            result.Add(new ConnectedDeviceInfo
            {
                InstanceGuid = dev.InstanceGuid,
                ProductGuid = dev.ProductGuid,
                InstanceName = dev.InstanceName,
                ProductName = dev.ProductName,
                DeviceType = dev.Type,
                HasFFB = hasFfb
            });
        }

        return result;
    }

    public IDirectInputDevice8? AcquireDevice(Guid instanceGuid, IntPtr windowHandle)
    {
        if (_acquiredDevices.TryGetValue(instanceGuid, out var existing))
            return existing;

        try
        {
            var device = _directInput.CreateDevice(instanceGuid);
            device.SetCooperativeLevel(windowHandle, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
            device.SetDataFormat<RawJoystickState>();
            device.Acquire();

            _acquiredDevices[instanceGuid] = device;
            return device;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to acquire device {instanceGuid}: {ex.Message}");
            return null;
        }
    }

    public bool PollDevice(Guid instanceGuid, PolledInputState outState)
    {
        if (!_acquiredDevices.TryGetValue(instanceGuid, out var device))
            return false;

        try
        {
            device.Poll();
            var state = device.GetCurrentJoystickState();

            outState.Axes[0] = state.X;
            outState.Axes[1] = state.Y;
            outState.Axes[2] = state.Z;
            outState.Axes[3] = state.RotationX;
            outState.Axes[4] = state.RotationY;
            outState.Axes[5] = state.RotationZ;

            var sliders = state.Sliders;
            if (sliders != null && sliders.Length > 0)
            {
                outState.Axes[6] = sliders[0];
                if (sliders.Length > 1)
                    outState.Axes[7] = sliders[1];
            }

            var buttons = state.Buttons;
            if (buttons != null)
            {
                int count = Math.Min(buttons.Length, outState.Buttons.Length);
                for (int i = 0; i < count; i++)
                    outState.Buttons[i] = buttons[i];
            }

            var pov = state.PointOfViewControllers;
            if (pov != null)
            {
                int count = Math.Min(pov.Length, outState.PointOfView.Length);
                for (int i = 0; i < count; i++)
                    outState.PointOfView[i] = (int)pov[i];
            }

            return true;
        }
        catch
        {
            // Device may have been disconnected or lost acquisition
            try { device.Acquire(); } catch { }
            return false;
        }
    }

    public void ReleaseAll()
    {
        foreach (var dev in _acquiredDevices.Values)
        {
            try
            {
                dev.Unacquire();
                dev.Dispose();
            }
            catch { }
        }
        _acquiredDevices.Clear();
    }

    public void Dispose()
    {
        ReleaseAll();
        _directInput.Dispose();
    }
}
