using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ForzaNext.Core.Virtual;

public class VirtualWheelDriver : IDisposable
{
    private const uint DeviceId = 1;
    private bool _isAcquired = false;
    private GCHandle _callbackHandle;
    private NativeVJoy.FfbCallback? _ffbCallback;

    public event Action<FfbPacketType, IntPtr, int>? FfbDataReceived;

    public bool IsAcquired => _isAcquired;

    public bool Initialize()
    {
        EnsureNativeDll();

        if (!NativeVJoy.vJoyEnabled())
        {
            Console.WriteLine("[ERROR] vJoy driver is not enabled in Windows.");
            return false;
        }

        short version = NativeVJoy.GetvJoyVersion();
        Console.WriteLine($"[INFO] vJoy driver detected. Version: 0x{version:X4}");

        var status = NativeVJoy.GetVJDStatus(DeviceId);
        Console.WriteLine($"[INFO] Device {DeviceId} status: {status}");

        if (status == VjdStatus.VJD_STAT_BUSY)
        {
            Console.WriteLine($"[WARNING] Device {DeviceId} is currently busy. Attempting reset...");
            NativeVJoy.RelinquishVJD(DeviceId);
        }

        if (!NativeVJoy.AcquireVJD(DeviceId))
        {
            Console.WriteLine($"[ERROR] Failed to acquire vJoy device {DeviceId}.");
            return false;
        }

        _isAcquired = true;
        NativeVJoy.ResetVJD(DeviceId);

        // Register FFB callback safely
        RegisterFfbCallback();

        Console.WriteLine($"[SUCCESS] Virtual Wheel Device {DeviceId} acquired and ready!");
        return true;
    }

    private void EnsureNativeDll()
    {
        // If vJoyInterface.dll is not in current AppDomain, copy from known locations
        string targetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vJoyInterface.dll");
        if (!File.Exists(targetPath))
        {
            string[] searchPaths = {
                @"C:\Program Files\vJoy\x64\vJoyInterface.dll",
                @"C:\forza_emuwheel_v1.4a\vJoyInterface.dll"
            };

            foreach (var p in searchPaths)
            {
                if (File.Exists(p))
                {
                    try
                    {
                        File.Copy(p, targetPath, true);
                        Console.WriteLine($"[INFO] Copied vJoyInterface.dll from {p}");
                        break;
                    }
                    catch { }
                }
            }
        }
    }

    private void RegisterFfbCallback()
    {
        try
        {
            _ffbCallback = OnNativeFfb;
            _callbackHandle = GCHandle.Alloc(_ffbCallback);
            NativeVJoy.FfbRegisterGenCB(_ffbCallback, IntPtr.Zero);
            Console.WriteLine("[INFO] FFB Callback registered successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARNING] Could not register FFB Callback: {ex.Message}");
        }
    }

    private void OnNativeFfb(IntPtr data, IntPtr userData)
    {
        if (data == IntPtr.Zero) return;

        try
        {
            uint res = NativeVJoy.Ffb_h_Type(data, out var packetType);
            if (res == 0) // ERROR_SUCCESS
            {
                NativeVJoy.Ffb_h_Packet(data, out _, out int dataSize, out IntPtr packetData);
                FfbDataReceived?.Invoke(packetType, packetData, dataSize);
            }
        }
        catch
        {
            // Suppress callback exceptions to prevent kernel-level unwinding crash
        }
    }

    public bool Feed(ref JoystickPositionV2 state)
    {
        if (!_isAcquired) return false;
        state.bDevice = (byte)DeviceId;
        return NativeVJoy.UpdateVJD(DeviceId, ref state);
    }

    public void Dispose()
    {
        if (_isAcquired)
        {
            NativeVJoy.ResetVJD(DeviceId);
            NativeVJoy.RelinquishVJD(DeviceId);
            _isAcquired = false;
        }

        if (_callbackHandle.IsAllocated)
            _callbackHandle.Free();
    }
}
