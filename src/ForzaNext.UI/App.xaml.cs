using System.Windows;
using ForzaNext.Core;

namespace ForzaNext.UI;

public partial class App : Application
{
    private Mutex? _instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        // Presentation-only entry points never initialize profiles, native devices or elevation.
        if (e.Args.Length == 2 && e.Args[0] == "--preview-check")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var window = new MainWindow(preview: true);
                var results = window.VerifyPreviewInteractions();
                System.IO.File.WriteAllText(System.IO.Path.GetFullPath(e.Args[1]), System.Text.Json.JsonSerializer.Serialize(results));
                Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--preview-render")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (e.Args.Length != 5 || !int.TryParse(e.Args[2], out int page) ||
                    !int.TryParse(e.Args[3], out int width) || !int.TryParse(e.Args[4], out int height))
                    throw new ArgumentException("--preview-render output.png page width height");
                var shell = new MainWindow(preview: true);
                Window preview = page switch
                {
                    -1 => new CalibrationWindow(null, new() { InstanceName = "ODrive-Wheel HID" }, new() { Id = "Steering", AxisIndex = 0 }, "Volante", preview: true),
                    -2 => new ProfileDetailsDialog(null, "Novo perfil", "Meu setup", "Forza Horizon"),
                    _ => shell
                };
                preview.Width = width; preview.Height = height;
                if (page >= 0) shell.SelectPage(page);
                var content = (FrameworkElement)preview.Content;
                if (content is System.Windows.Controls.Panel panel) panel.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "CanvasBrush");
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = System.IO.File.Create(System.IO.Path.GetFullPath(e.Args[1]));
                encoder.Save(output);
                Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] == "--preview")
        {
            base.OnStartup(e);
            MainWindow = new MainWindow(preview: true);
            MainWindow.Show();
            return;
        }
        DiagnosticLog.Write("Process startup", $"PID={Environment.ProcessId}; Args={string.Join(" | ", e.Args)}");
        if (e.Args.Length == 3 && e.Args[0] == "--vjoy-lifecycle" &&
            e.Args[1].StartsWith(ForzaNext.Core.Virtual.VJoyDeviceLease.PipePrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(e.Args[1][ForzaNext.Core.Virtual.VJoyDeviceLease.PipePrefix.Length..], "N", out _) &&
            int.TryParse(e.Args[2], out int lifecycleParent))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int result = await ForzaNext.Core.Virtual.VJoyDeviceLease.RunGuardianAsync(e.Args[1], lifecycleParent);
            Environment.Exit(result);
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--device-worker" &&
            e.Args[1].StartsWith("ForzaNext-", StringComparison.Ordinal) &&
            Guid.TryParseExact(e.Args[1][10..], "N", out _) && int.TryParse(e.Args[2], out int parentId))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int result = await DeviceWorkerHost.RunAsync(e.Args[1], parentId);
            Environment.Exit(result);
            return;
        }
        _instance = new Mutex(false, @"Local\ForzaNextWheel.UI");
        bool owns;
        try { owns = _instance.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
        if (!owns) { _instance.Dispose(); _instance = null; MessageBox.Show("O ForzaNext já está aberto.", "ForzaNext"); Shutdown(); return; }
        
        // Proactively uncloak devices on startup in case a previous session crashed unexpectedly
        if (ForzaNext.Core.Devices.HidHideService.IsInstalled)
        {
            try { ForzaNext.Core.Devices.HidHideService.UncloakSync(); } catch { }
        }

        Exit += (_, _) =>
        {
            if (ForzaNext.Core.Devices.HidHideService.IsInstalled)
            {
                try { ForzaNext.Core.Devices.HidHideService.UncloakSync(); } catch { }
            }
            _instance.ReleaseMutex();
            _instance.Dispose();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (ForzaNext.Core.Devices.HidHideService.IsInstalled)
            {
                try { ForzaNext.Core.Devices.HidHideService.UncloakSync(); } catch { }
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            DiagnosticLog.Write("Unhandled", ev.ExceptionObject.ToString() ?? "Unknown error");
            if (ForzaNext.Core.Devices.HidHideService.IsInstalled)
            {
                try { ForzaNext.Core.Devices.HidHideService.UncloakSync(); } catch { }
            }
        };
        DispatcherUnhandledException += async (_, ev) =>
        {
            DiagnosticLog.Write("UI exception", ev.Exception.ToString());
            ev.Handled = true;
            if (ForzaNext.Core.Devices.HidHideService.IsInstalled)
            {
                try { ForzaNext.Core.Devices.HidHideService.UncloakSync(); } catch { }
            }
            if (MainWindow is MainWindow window) await window.HandleFailureAsync(ev.Exception);
            MessageBox.Show($"A operação foi interrompida: {ev.Exception.Message}", "ForzaNext");
        };
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
