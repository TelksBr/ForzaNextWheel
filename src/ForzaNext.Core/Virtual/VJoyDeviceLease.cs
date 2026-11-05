using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ForzaNext.Core.Virtual;

// One elevated guardian per application lifetime, so closing needs no additional UAC prompt.
public sealed class VJoyDeviceLease : IVJoyDeviceLease
{
    public const string PipePrefix = "ForzaNext-Lifecycle-";
    private readonly NamedPipeServerStream _pipe;
    private readonly string _name = PipePrefix + Guid.NewGuid().ToString("N");
    private Process? _helper;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private int _stopping;
    public bool IsActive => _stopping == 0 && _helper is { HasExited: false };

    private VJoyDeviceLease()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("Usuário Windows não identificado.");
        var security = CreatePipeSecurity(user);
        _pipe = NamedPipeServerStreamAcl.Create(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security);
    }

    internal static PipeSecurity CreatePipeSecurity(SecurityIdentifier user)
    {
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return security;
    }

    public static async Task<VJoyDeviceLease> OpenAsync()
    {
        var lease = new VJoyDeviceLease();
        try { await lease.StartAsync(); return lease; }
        catch { lease._pipe.Dispose(); lease._helper?.Dispose(); throw; }
    }

    private async Task StartAsync()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("Executável atual não encontrado."))
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        info.ArgumentList.Add("--vjoy-lifecycle"); info.ArgumentList.Add(_name); info.ArgumentList.Add(Environment.ProcessId.ToString());
        try { _helper = await Task.Run(() => Process.Start(info) ?? throw new IOException("O Windows não iniciou o gerenciador do volante virtual.")); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new IOException("A autorização do Windows foi cancelada. Clique em Iniciar para tentar novamente.", ex); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        while (true)
        {
            var connection = _pipe.WaitForConnectionAsync(timeout.Token);
            if (await Task.WhenAny(connection, _helper.WaitForExitAsync(timeout.Token)) != connection)
                throw new IOException("O gerenciador do volante virtual encerrou antes de conectar.");
            await connection;
            if (GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var client) && client == _helper.Id) break;
            _pipe.Disconnect();
        }
        _reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, true);
        var line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromMinutes(11))
            ?? throw new IOException("O gerenciador desconectou durante a preparação do driver.");
        var message = JsonSerializer.Deserialize<WorkerMessage>(line, WorkerMessage.Json) ?? throw new IOException("Resposta inválida do gerenciador.");
        var result = message.Read<VJoySetupResult>();
        if (message.Kind != "lease-ready" || !result.Success) throw new IOException(result.Message);
    }

    public async Task<VJoySetupResult> StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return new(true, false, false, "Encerramento já solicitado.");
        try
        {
            await _writer!.WriteLineAsync(WorkerMessage.Encode("lease-stop", true));
            await _writer.FlushAsync();
            var line = await _reader!.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var message = line == null ? null : JsonSerializer.Deserialize<WorkerMessage>(line, WorkerMessage.Json);
            return message?.Kind == "lease-stopped" ? message.Read<VJoySetupResult>()
                : new(false, false, false, "O Windows não confirmou a desativação do vJoy 1. Confira o log da aplicação.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("vJoy lease stop", ex.ToString());
            return new(false, false, false, "Não foi possível confirmar a desativação do vJoy 1: " + ex.Message);
        }
        finally
        {
            // Do not kill the guardian while it may still be completing a Windows device operation.
            _pipe.Dispose();
            _helper?.Dispose();
        }
    }

    // WPF's elevated helper invokes this on its dispatcher thread; the mutex stays on that thread.
    public static async Task<int> RunGuardianAsync(string pipeName, int parentId)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 740;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        StreamWriter? writer = null;
        VJoyDeviceTarget? target = null;
        bool ownsMutex = false;
        using var mutex = new Mutex(false, @"Global\ForzaNextWheel-VJoy1-Lifecycle");
        VJoySetupResult stopped = new(true, false, false, "vJoy 1 desativado.");
        async Task Send(string kind, VJoySetupResult value)
        {
            await writer!.WriteLineAsync(WorkerMessage.Encode(kind, value));
            await writer.FlushAsync();
        }
        try
        {
            await pipe.ConnectAsync(10000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != parentId)
                throw new IOException("O canal de controle não pertence à aplicação que solicitou a operação.");
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            using var parent = Process.GetProcessById(parentId);
            try { ownsMutex = mutex.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
            {
                await Send("lease-ready", new(false, false, false, "Outra instância do ForzaNext já gerencia o vJoy 1."));
                return 2;
            }
            target = VJoyDeviceNode.Load();
            if (target != null && VJoyDeviceNode.Enable(target)) await Task.Delay(1000);
            bool install = !File.Exists(VJoySetupService.ConfigurationTool);
            int prepared = await VJoySetupService.RunElevatedAsync(install);
            if (prepared != 0)
            {
                await Send("lease-ready", new(false, prepared == 3010, false, prepared == 3010
                    ? "O driver foi instalado. Reinicie o Windows e reabra a aplicação para concluir a preparação."
                    : prepared == 2 ? "vJoy 1 está em uso por outro programa. Feche o outro feeder e tente novamente."
                    : "O Windows não concluiu a preparação do vJoy. Consulte o log da aplicação."));
                return prepared;
            }
            var inspection = await VJoySetupService.InspectAsync();
            if (!inspection.ConfigurationReady || inspection.Busy) throw new IOException(inspection.Summary);
            target = VJoyDeviceNode.FindDeviceOne();
            VJoyDeviceNode.Save(target);
            await Send("lease-ready", new(true, false, false, "Volante virtual preparado e ativo enquanto a aplicação estiver aberta."));
            var command = reader.ReadLineAsync();
            var parentExit = parent.WaitForExitAsync();
            if (await Task.WhenAny(command, parentExit) == command)
            {
                var line = await command;
                if (line != null && JsonSerializer.Deserialize<WorkerMessage>(line, WorkerMessage.Json)?.Kind != "lease-stop")
                    throw new IOException("Comando de ciclo de vida inválido.");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("vJoy guardian", ex.ToString());
            stopped = new(false, false, false, ex.Message);
            if (writer != null)
                try { await Send("lease-ready", stopped); } catch (Exception error) { DiagnosticLog.Write("vJoy guardian IPC", error.Message); }
        }
        finally
        {
            if (ownsMutex)
            {
                if (target != null)
                {
                    try
                    {
                        // Give our device worker time to release the virtual handle after a parent crash.
                        for (int attempt = 0; attempt < 30 && NativeVJoy.GetVJDStatus(1) == VjdStatus.VJD_STAT_BUSY; attempt++)
                            await Task.Delay(100);
                        if (NativeVJoy.GetVJDStatus(1) is VjdStatus.VJD_STAT_BUSY or VjdStatus.VJD_STAT_OWN)
                            throw new IOException("Outro processo ainda usa o vJoy 1; ele não foi desativado.");
                        VJoyDeviceNode.Disable(target);
                    }
                    catch (Exception ex) { DiagnosticLog.Write("Disable vJoy 1", ex.ToString()); stopped = new(false, false, false, ex.Message); }
                }
                if (writer != null)
                    try { await Send("lease-stopped", stopped); } catch (Exception ex) { DiagnosticLog.Write("vJoy guardian stop response", ex.Message); }
                mutex.ReleaseMutex();
            }
        }
        return stopped.Success ? 0 : 1;
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
