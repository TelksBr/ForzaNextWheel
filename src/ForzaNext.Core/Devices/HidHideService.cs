using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;
using ForzaNext.Core.Config;
using ForzaNext.Core.FFB;

namespace ForzaNext.Core.Devices;

public static class HidHideService
{
    private static readonly string[] CliCandidates = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nefarius Software Solutions e.U", "HidHideCLI", "HidHideCLI.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nefarius Software Solutions", "HidHideCLI", "HidHideCLI.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nefarius Software Solutions e.U", "HidHide", "x64", "HidHideCLI.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nefarius Software Solutions", "HidHide", "x64", "HidHideCLI.exe")
    };

    public static string? FindCliPath() => CliCandidates.FirstOrDefault(File.Exists);

    public static bool IsInstalled => FindCliPath() != null;

    /// <summary>
    /// Enumerates HID device instance paths from the Windows Registry (HKLM\SYSTEM\CurrentControlSet\Enum\HID).
    /// Instantly returns physical game controllers without any process spawn or pipe deadlocks.
    /// </summary>
    public static List<string> DiscoverGamingDevicePaths(IEnumerable<ControllerProfile>? profiles = null)
    {
        var paths = new List<string>();
        try
        {
            using var hidKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID");
            if (hidKey == null) return paths;

            // Collect VID/PID substrings from configured profiles
            var targetVidPids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (profiles != null)
            {
                foreach (var p in profiles)
                {
                    string hex = p.ProductGuid.ToString("N");
                    string pid = hex[..4];
                    string vid = hex.Substring(4, 4);
                    targetVidPids.Add($"VID_{vid}&PID_{pid}");
                }
            }

            foreach (var subName in hidKey.GetSubKeyNames())
            {
                // NEVER hide vJoy (HIDCLASS or VID 1234 PID BEAD)
                if (subName.Contains("HIDCLASS", StringComparison.OrdinalIgnoreCase) ||
                    (subName.Contains("1234", StringComparison.OrdinalIgnoreCase) && subName.Contains("BEAD", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                using var subKey = hidKey.OpenSubKey(subName);
                if (subKey == null) continue;

                foreach (var instName in subKey.GetSubKeyNames())
                {
                    using var instKey = subKey.OpenSubKey(instName);
                    if (instKey == null) continue;

                    string fullPath = $@"HID\{subName}\{instName}";
                    string deviceDesc = instKey.GetValue("DeviceDesc") as string ?? "";
                    string friendlyName = instKey.GetValue("FriendlyName") as string ?? "";
                    string[] hardwareIds = instKey.GetValue("HardwareID") as string[] ?? Array.Empty<string>();
                    string hwJoined = string.Join(" ", hardwareIds);

                    bool isGamingDevice = deviceDesc.Contains("game", StringComparison.OrdinalIgnoreCase)
                                       || deviceDesc.Contains("joystick", StringComparison.OrdinalIgnoreCase)
                                       || deviceDesc.Contains("gamepad", StringComparison.OrdinalIgnoreCase)
                                       || friendlyName.Contains("Wheel", StringComparison.OrdinalIgnoreCase)
                                       || friendlyName.Contains("Shifter", StringComparison.OrdinalIgnoreCase)
                                       || friendlyName.Contains("BotonBox", StringComparison.OrdinalIgnoreCase)
                                       || subName.Contains("SHIFTERVHID", StringComparison.OrdinalIgnoreCase);

                    bool isProfileMatch = targetVidPids.Any(vp => subName.Contains(vp, StringComparison.OrdinalIgnoreCase) || hwJoined.Contains(vp, StringComparison.OrdinalIgnoreCase));

                    if (isGamingDevice || isProfileMatch)
                    {
                        paths.Add(fullPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("HidHide Registry", "Erro ao enumerar dispositivos HID: " + ex.Message);
        }
        return paths;
    }

    public static async Task<bool> ConfigureCloakingAsync(IEnumerable<ControllerProfile>? profiles = null, CancellationToken token = default)
    {
        string? cli = FindCliPath();
        if (cli == null)
        {
            DiagnosticLog.Write("HidHide", "HidHideCLI não encontrado. Ocultamento de dispositivos físicos ignorado.");
            return false;
        }

        try
        {
            var args = new List<string>();

            // 1. Whitelist current application processes
            var whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Environment.ProcessPath is { } procPath && File.Exists(procPath))
                whitelist.Add(procPath);

            try
            {
                var mainModule = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(mainModule) && File.Exists(mainModule))
                    whitelist.Add(mainModule);
            }
            catch { }

            string dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (File.Exists(dotnet)) whitelist.Add(dotnet);

            string baseDir = AppContext.BaseDirectory;
            string[] possibleExes = new[]
            {
                Path.Combine(baseDir, "ForzaNext.Cli.exe"),
                Path.Combine(baseDir, "ForzaNext.UI.exe"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "ForzaNext.Cli", "bin", "Debug", "net8.0-windows", "ForzaNext.Cli.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "ForzaNext.Cli", "bin", "Release", "net8.0-windows", "ForzaNext.Cli.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "ForzaNext.UI", "bin", "Debug", "net8.0-windows", "ForzaNext.UI.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "ForzaNext.UI", "bin", "Release", "net8.0-windows", "win-x64", "publish", "ForzaNext.UI.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "ForzaNext.UI", "bin", "Release", "net8.0-windows", "ForzaNext.UI.exe"))
            };
            foreach (var exe in possibleExes)
            {
                if (File.Exists(exe)) whitelist.Add(exe);
            }

            foreach (var app in whitelist)
            {
                args.Add("--app-reg");
                args.Add(app);
            }

            // 2. Unregister known Forza executables from whitelist so they NEVER see cloaked physical devices
            string[] gamesToUnreg = new[]
            {
                @"C:\Games\Forza Horizon 6\forzahorizon6.exe",
                @"C:\Games\Forza Horizon 5\ForzaHorizon5.exe",
                @"C:\Games\Forza Horizon 4\ForzaHorizon4.exe",
                @"C:\Games\Forza Motorsport\forzamotorsport.exe"
            };
            foreach (var g in gamesToUnreg)
            {
                args.Add("--app-unreg");
                args.Add(g);
            }

            // 3. Discover and hide physical game controllers
            var pathsToHide = DiscoverGamingDevicePaths(profiles);

            foreach (var path in pathsToHide)
            {
                args.Add("--dev-hide");
                args.Add(path);
                DiagnosticLog.Write("HidHide", $"Ocultando dispositivo físico: {path}");
                FfbDebugLogger.Log("HIDHIDE-HIDE", path);
            }

            // 4. Activate cloaking
            args.Add("--cloak-on");

            bool success = await RunCliAsync(cli, args, token);
            if (success)
            {
                DiagnosticLog.Write("HidHide", $"Cloaking ativado com sucesso ({pathsToHide.Count} dispositivos físicos ocultados).");
                FfbDebugLogger.Log("HIDHIDE-SUCCESS", $"Cloaked {pathsToHide.Count} physical gaming devices.");
            }
            return success;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("HidHide", "Falha ao configurar cloaking: " + ex.Message);
            FfbDebugLogger.LogError("HIDHIDE-ERROR", "Falha ao configurar cloaking", ex);
            return false;
        }
    }

    public static async Task<bool> UncloakAsync(IEnumerable<ControllerProfile>? profiles = null, CancellationToken token = default)
    {
        string? cli = FindCliPath();
        if (cli == null) return false;

        try
        {
            var args = new List<string> { "--cloak-off" };
            var pathsToUnhide = DiscoverGamingDevicePaths(profiles);
            foreach (var path in pathsToUnhide)
            {
                args.Add("--dev-unhide");
                args.Add(path);
            }
            bool success = await RunCliAsync(cli, args, token);
            if (success)
            {
                DiagnosticLog.Write("HidHide", $"Cloaking desativado ({pathsToUnhide.Count} dispositivos liberados).");
                FfbDebugLogger.Log("HIDHIDE-UNCLOAK", $"Unhid {pathsToUnhide.Count} devices and turned cloak-off.");
            }
            return success;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("HidHide", "Falha ao desativar cloaking: " + ex.Message);
            return false;
        }
    }

    public static bool UncloakSync(IEnumerable<ControllerProfile>? profiles = null)
    {
        string? cli = FindCliPath();
        if (cli == null) return false;

        try
        {
            var args = new List<string> { "--cloak-off" };
            var pathsToUnhide = DiscoverGamingDevicePaths(profiles);
            foreach (var path in pathsToUnhide)
            {
                args.Add("--dev-unhide");
                args.Add(path);
            }
            bool success = RunCliSync(cli, args);
            if (success)
            {
                DiagnosticLog.Write("HidHide", $"Cloaking desativado sincronicamente ({pathsToUnhide.Count} dispositivos liberados).");
                FfbDebugLogger.Log("HIDHIDE-UNCLOAK-SYNC", $"Unhid {pathsToUnhide.Count} devices and turned cloak-off.");
            }
            return success;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("HidHide", "Falha ao desativar cloaking: " + ex.Message);
            return false;
        }
    }

    private static bool RunCliSync(string cli, List<string> args)
    {
        try
        {
            bool isElevated = false;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                isElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { }

            string argumentsString = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            var info = new ProcessStartInfo(cli, argumentsString)
            {
                CreateNoWindow = true
            };

            if (isElevated)
            {
                info.UseShellExecute = false;
            }
            else
            {
                info.UseShellExecute = true;
                info.Verb = "runas";
                info.WindowStyle = ProcessWindowStyle.Hidden;
            }

            using var process = Process.Start(info);
            if (process == null) return false;
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("HidHide CLI", ex.ToString());
            return false;
        }
    }

    private static async Task<bool> RunCliAsync(string cli, List<string> args, CancellationToken token)
    {
        try
        {
            bool isElevated = false;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                isElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { }

            string argumentsString = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            var info = new ProcessStartInfo(cli, argumentsString)
            {
                CreateNoWindow = true
            };

            if (isElevated)
            {
                info.UseShellExecute = false;
            }
            else
            {
                info.UseShellExecute = true;
                info.Verb = "runas";
                info.WindowStyle = ProcessWindowStyle.Hidden;
            }

            using var process = Process.Start(info);
            if (process == null) return false;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            await process.WaitForExitAsync(linked.Token);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("HidHide CLI", ex.ToString());
            return false;
        }
    }
}
