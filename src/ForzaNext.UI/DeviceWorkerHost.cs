using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Interop;
using ForzaNext.Core;
using ForzaNext.Core.FFB;
using ForzaNext.Core.Virtual;
using ForzaNext.Core.Devices;

namespace ForzaNext.UI;

internal static class DeviceWorkerHost
{
    public static async Task<int> RunAsync(string pipeName, int parentId)
    {
        using var lifetime = new CancellationTokenSource();
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var writeLock = new SemaphoreSlim(1);
        LocalWheelSession? session = null;
        PhysicalInputObserver? observation = null;
        HwndSource? window = null;
        StreamWriter? writer = null;
        async Task Send<T>(string kind, T value)
        {
            await writeLock.WaitAsync(lifetime.Token);
            try
            {
                await writer!.WriteLineAsync(WorkerMessage.Encode(kind, value).AsMemory(), lifetime.Token);
                await writer.FlushAsync(lifetime.Token);
            }
            finally { writeLock.Release(); }
        }
        async Task WatchParent()
        {
            try
            {
                using var parent = Process.GetProcessById(parentId);
                await parent.WaitForExitAsync(lifetime.Token);
                lifetime.Cancel();
            }
            catch (OperationCanceledException) { }
            catch (ArgumentException) { lifetime.Cancel(); }
        }
        async Task AnnounceReady()
        {
            try
            {
                await session!.Started;
                await Send("ready", new WorkerReady(session.ResolvedProfiles, session.HasFfb, session.FfbDevice, session.Telemetry));
            }
            catch (Exception ex)
            {
                try { await Send("error", ex.Message); } catch (Exception error) { DiagnosticLog.Write("Worker IPC", error.Message); }
                lifetime.Cancel();
            }
        }
        async Task PublishTelemetry()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await Send("telemetry", session!.Telemetry);
                    if (session.Completion.IsCompleted)
                    {
                        await Send("error", session.Telemetry.Status);
                        lifetime.Cancel();
                        return;
                    }
                    await Task.Delay(100, lifetime.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { DiagnosticLog.Write("Worker telemetry", ex.Message); lifetime.Cancel(); }
        }
        async Task Test(WorkerTest request)
        {
            string? error = null;
            bool canceled = false;
            try { await session!.TestAsync(request.Type); }
            catch (OperationCanceledException) { canceled = true; }
            catch (Exception ex) { error = ex.Message; }
            try { await Send("test-result", new WorkerTestResult(request.Id, error, canceled)); }
            catch (Exception ex) { DiagnosticLog.Write("Worker test response", ex.Message); }
        }
        try
        {
            _ = WatchParent();
            await pipe.ConnectAsync(3000, lifetime.Token);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            var line = await reader.ReadLineAsync(lifetime.Token) ?? throw new IOException("Supervisor desconectado.");
            var message = JsonSerializer.Deserialize<WorkerMessage>(line, WorkerMessage.Json) ?? throw new IOException("Mensagem inválida.");
            if (message.Kind == "probe")
            {
                var result = await Task.Run(VirtualWheelDriver.Inspect);
                await Send("probe-result", result);
                return 0;
            }
            if (message.Kind == "capture")
            {
                window = new HwndSource(new HwndSourceParameters("ForzaNext input capture")
                { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80 });
                observation = new PhysicalInputObserver(message.Read<InputCaptureRequest>(), window.Handle);
                async Task PublishInputs()
                {
                    try
                    {
                        while (!lifetime.IsCancellationRequested)
                        {
                            if (observation.Completion.IsCompleted) { await Send("error", observation.Error ?? "Leitura encerrada."); return; }
                            if (observation.Latest is { } sample) await Send("input-sample", sample);
                            await Task.Delay(33, lifetime.Token);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { DiagnosticLog.Write("Input capture IPC", ex.Message); }
                }
                _ = PublishInputs();
                await reader.ReadLineAsync(lifetime.Token); // the parent only sends stop in capture mode
                return 0;
            }
            if (message.Kind != "start") throw new IOException("Esperada configuração inicial.");
            var request = message.Read<WorkerStart>();
            // Cooperative-level HWND belongs to this process, with no visible window.
            window = new HwndSource(new HwndSourceParameters("ForzaNext device worker")
            { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80 });
            session = new LocalWheelSession(request.Profiles, window.Handle, request.Configuration, request.EnableFfb);
            _ = AnnounceReady();
            _ = PublishTelemetry();
            while (await reader.ReadLineAsync(lifetime.Token) is { } command)
            {
                var next = JsonSerializer.Deserialize<WorkerMessage>(command, WorkerMessage.Json) ?? throw new IOException("Comando inválido.");
                switch (next.Kind)
                {
                    case "config": session.UpdateConfiguration(next.Read<FFBConfiguration>()); break;
                    case "test": _ = Test(next.Read<WorkerTest>()); break;
                    case "stop": return 0;
                }
            }
            return 0;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 0; }
        catch (Exception ex) { DiagnosticLog.Write("Worker host", ex.ToString()); return 1; }
        finally
        {
            lifetime.Cancel();
            if (ForzaNext.Core.Devices.HidHideService.IsInstalled)
            {
                try { ForzaNext.Core.Devices.HidHideService.UncloakSync(); } catch { }
            }
            if (observation != null)
            {
                try { await observation.StopAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (TimeoutException)
                {
                    try { ForzaNext.Core.Devices.HidHideService.UncloakSync(); } catch { }
                    Environment.Exit(2);
                }
            }
            if (session != null)
            {
                try { await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (TimeoutException)
                {
                    DiagnosticLog.Write("Worker exit", "Chamada nativa bloqueada; encerrando processo isolado.");
                    try { ForzaNext.Core.Devices.HidHideService.UncloakSync(); } catch { }
                    Environment.Exit(2);
                }
                catch (Exception ex) { DiagnosticLog.Write("Worker cleanup", ex.ToString()); }
            }
            window?.Dispose();
        }
    }
}
