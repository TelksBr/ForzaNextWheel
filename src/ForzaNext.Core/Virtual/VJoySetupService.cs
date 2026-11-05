using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace ForzaNext.Core.Virtual;

public sealed record VJoySetupResult(bool Success, bool RestartRequired, bool Canceled, string Message);

public static class VJoySetupService
{
    public const string InstallerVersion = "2.2.2.0";
    public const string InstallerSha256 = "EF569A3105CD301B89580F18F60C66B339E95296ACF2C0DFCAF4B4BBF8AB68FE";
    public const string InstallerUrl = "https://github.com/BrunnerInnovation/vJoy/releases/download/v2.2.2.0/vJoySetup_v2.2.2.0_Win10_Win11.exe";
    public const string InstallerResource = "ForzaNext.Dependencies.vJoySetup.exe";
    private static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy", "x64");
    public static string ConfigurationTool => Path.Combine(InstallDirectory, "vJoyConfig.exe");
    public static string BackupDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ForzaNextWheel", "Backups");
    public static IReadOnlyList<string> ConfigurationArguments => new[]
        { "1", "-f", "-a", "X", "Y", "Z", "RX", "RY", "RZ", "SL0", "SL1", "-b", "128", "-p", "1", "-e", "all" };

    public static async Task<VJoyInspection> InspectAsync(CancellationToken token = default)
    {
        var worker = new DeviceWorkerProcess();
        try { return await worker.InspectAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new(File.Exists(ConfigurationTool), false, false, VjdStatus.VJD_STAT_UNKN, 0, 0, Array.Empty<string>(), false, "Não foi possível verificar o driver: " + ex.Message); }
        finally { await worker.AbortAsync(); }
    }

    // Called by the authorized lifecycle guardian, never by read-only detection or device polling.
    public static async Task<int> RunElevatedAsync(bool install)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 740;
            if (!install)
            {
                var current = VirtualWheelDriver.Inspect();
                if (current.Busy) return 2;
                if (current.ConfigurationReady) return 0;
            }
            await BackupConfigurationAsync();
            if (install)
            {
                string installer = await GetVerifiedInstallerAsync(CancellationToken.None);
                int result = await RunAsync(installer,
                    new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/RESTARTEXITCODE=3010", "/SP-" }, TimeSpan.FromMinutes(10), killOnTimeout: false);
                if (result == 3010) return 3010;
                if (result != 0) throw new IOException($"Instalador vJoy retornou {result}.");
            }
            if (!File.Exists(ConfigurationTool)) throw new FileNotFoundException("Instalação vJoy não encontrada. Use Instalar e preparar.");
            var before = VirtualWheelDriver.Inspect();
            if (before.Busy) return 2;
            if (!before.Enabled)
            {
                int enable = await RunAsync(ConfigurationTool, new[] { "enable", "on" }, TimeSpan.FromSeconds(30));
                if (enable != 0) throw new IOException("Não foi possível ativar o driver vJoy.");
            }
            int configured = await RunAsync(ConfigurationTool, ConfigurationArguments, TimeSpan.FromSeconds(45));
            if (configured != 0) throw new IOException($"Configurador vJoy retornou {configured}.");
            // The parent verifies real capabilities in a fresh isolated process; CLI exit 0 is insufficient.
            return 0;
        }
        catch (Exception ex) { DiagnosticLog.Write("Elevated vJoy setup", ex.ToString()); return 1; }
    }

    private static async Task BackupConfigurationAsync()
    {
            Directory.CreateDirectory(BackupDirectory);
            using (var parameters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\vjoy\Parameters"))
            {
                if (parameters != null)
                {
                    string backup = Path.Combine(BackupDirectory, $"vJoy-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.reg");
                    int result = await RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"),
                        new[] { "export", @"HKLM\SYSTEM\CurrentControlSet\Services\vjoy\Parameters", backup, "/y" }, TimeSpan.FromSeconds(10));
                    if (result != 0) throw new IOException("Backup da configuração vJoy falhou. Alteração cancelada.");
                }
            }
    }

    private static async Task<int> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout, bool killOnTimeout = true)
    {
        var info = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Não foi possível iniciar " + Path.GetFileName(executable));
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(timeout); }
        catch (TimeoutException)
        {
            if (killOnTimeout && !process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        DiagnosticLog.Write(Path.GetFileName(executable), await output + await error);
        return process.ExitCode;
    }

    internal static async Task<string> GetVerifiedInstallerAsync(CancellationToken token)
    {
        var directory = Path.Combine(AppPaths.DataDirectory, "Dependencies");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "vJoySetup-" + InstallerVersion + ".exe");
        if (File.Exists(destination) && await InstallerMatchesAsync(destination, token)) return destination;
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var embedded = Assembly.GetEntryAssembly()?.GetManifestResourceStream(InstallerResource);
                if (embedded != null) await embedded.CopyToAsync(output, token);
                else
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                    using var response = await client.GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is long length && length != 5302312) throw new InvalidDataException("Tamanho inesperado do instalador oficial.");
                    await response.Content.CopyToAsync(output, token);
                }
            }
            if (!await InstallerMatchesAsync(temporary, token)) throw new InvalidDataException("O instalador não corresponde ao SHA-256 fixado. Execução bloqueada.");
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task<bool> InstallerMatchesAsync(string path, CancellationToken token = default)
    {
        using var stream = File.OpenRead(path);
        var actual = await SHA256.HashDataAsync(stream, token);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(InstallerSha256));
    }
}
