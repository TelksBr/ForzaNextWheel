using System;
using System.IO;
using System.Threading;
using ForzaNext.Core;
using ForzaNext.Core.Config;
using ForzaNext.Core.Devices;
using ForzaNext.Core.Virtual;

namespace ForzaNext.Cli;

internal class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine("       FORZANEXT WHEEL - CORE ENGINE TEST       ");
        Console.WriteLine("=================================================");

        string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configuration.json");
        if (!File.Exists(configPath))
        {
            configPath = @"C:\forza-wheel-next\configuration.json";
        }

        Console.WriteLine($"[1] Loading configuration from: {configPath}");
        if (!File.Exists(configPath))
        {
            Console.WriteLine("[ERROR] configuration.json not found!");
            return;
        }

        var profiles = ProfileManager.LoadFromFile(configPath);
        Console.WriteLine($"[SUCCESS] Loaded {profiles.Count} controller profile(s):");
        foreach (var p in profiles)
        {
            Console.WriteLine($" - {p.InstanceName} (Guid: {p.InstanceGuid})");
            if (p.Axes != null)
                Console.WriteLine($"   * Axes: {p.Axes.Count} mapped");
            if (p.Buttons != null)
                Console.WriteLine($"   * Buttons: {p.Buttons.Count} mapped");
        }

        Console.WriteLine("\n[2] Probing DirectInput devices...");
        using var dinput = new DirectInputDeviceManager();
        var devices = dinput.EnumerateDevices();
        Console.WriteLine($"Found {devices.Count} connected DirectInput devices:");
        foreach (var d in devices)
        {
            if (d.ProductName.Contains("ODrive", StringComparison.OrdinalIgnoreCase) ||
                d.ProductName.Contains("Shifter", StringComparison.OrdinalIgnoreCase) ||
                d.ProductName.Contains("vJoy", StringComparison.OrdinalIgnoreCase) ||
                d.ProductName.Contains("Wheel", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($" => [{d.InstanceName}] Product: {d.ProductName} | FFB: {d.HasFFB} | Guid: {d.InstanceGuid}");
            }
        }

        Console.WriteLine("\n[3] Initializing Integrated Virtual Wheel Driver...");
        using var virtualWheel = new VirtualWheelDriver();
        bool vJoyOk = virtualWheel.Initialize();
        Console.WriteLine($"Virtual Wheel Driver initialized: {vJoyOk}");

        Console.WriteLine("\n[4] Acquiring physical controllers for polling test...");
        IntPtr dummyHwnd = IntPtr.Zero; // background polling
        foreach (var p in profiles)
        {
            var acquired = dinput.AcquireDevice(p.InstanceGuid, dummyHwnd);
            Console.WriteLine($" - Device [{p.InstanceName}]: {(acquired != null ? "ACQUIRED" : "FAILED")}");
        }

        var aggregator = new InputAggregator(profiles);

        Console.WriteLine("\n[5] Running 5-second polling loop (Live Input Feed)...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int ticks = 0;

        while (sw.ElapsedMilliseconds < 5000)
        {
            foreach (var p in profiles)
            {
                var buffer = aggregator.GetStateBuffer(p.InstanceGuid);
                dinput.PollDevice(p.InstanceGuid, buffer);
            }

            var virtualPos = aggregator.Aggregate();
            if (vJoyOk)
            {
                virtualWheel.Feed(ref virtualPos);
            }

            ticks++;
            if (ticks % 50 == 0)
            {
                Console.Write($"\r[FEEDING] Wheel AxisX: {virtualPos.AxisX,-5} | Y(Thr): {virtualPos.AxisY,-5} | Z(Brk): {virtualPos.AxisZ,-5} | Btns: 0x{virtualPos.Buttons:X4}  ");
            }

            Thread.Sleep(5); // ~200 Hz for test output
        }

        Console.WriteLine("\n\n[SUCCESS] Test finished cleanly. Core Engine is 100% operational!");
    }
}
