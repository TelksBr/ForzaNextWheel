using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ForzaNext.Core.Config;
using ForzaNext.Core.FFB;
using ForzaNext.Core.Virtual;
using ForzaNext.Core.Devices;

namespace ForzaNext.Core;

public sealed record WorkerStart(List<ControllerProfile> Profiles, FFBConfiguration Configuration, bool EnableFfb);
public sealed record WorkerReady(List<ControllerProfile> Profiles, bool HasFfb, Guid? FfbDevice, WheelTelemetry Telemetry);
public sealed record WorkerTest(Guid Id, MotorTest Type);
public sealed record WorkerTestResult(Guid Id, string? Error, bool Canceled);
public sealed record WorkerMessage(string Kind, JsonElement Value)
{
    public static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    public static string Encode<T>(string kind, T value) => JsonSerializer.Serialize(new WorkerMessage(kind, JsonSerializer.SerializeToElement(value, Json)), Json);
    public T Read<T>() => Value.Deserialize<T>(Json) ?? throw new InvalidDataException("Mensagem de worker inválida.");
}

internal sealed class DeviceWorkerProcess : IWheelWorker
{
    private readonly NamedPipeServerStream _pipe;
    private readonly string _pipeName = "ForzaNext-" + Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _writeLock = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<WorkerReady> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<VJoyInspection> _inspection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isProbe;
    private bool _isCapture;
    private readonly TaskCompletionSource _captureReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private InputSample? _latestInput;
    public InputSample? LatestInput => Volatile.Read(ref _latestInput);
    public async Task StartCaptureAsync(InputCaptureRequest request, CancellationToken token)
    {
        _isCapture = true;
        await ConnectAsync(token);
        await SendAsync("capture", request, token);
        await _captureReady.Task.WaitAsync(token);
    }
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _tests = new();
    private StreamWriter? _writer;
    private Process? _process;
    private WheelTelemetry _telemetry = new(InputAggregator.NeutralPosition(), 0, 0, 0, "Aguardando processo de dispositivos");
    private WorkerReady? _info;
    public Task Completion => _completion.Task;
    public WheelTelemetry Telemetry => Volatile.Read(ref _telemetry);
    public List<ControllerProfile> Profiles => _info?.Profiles ?? new();
    public bool HasFfb => _info?.HasFfb == true;
    public Guid? FfbDevice => _info?.FfbDevice;

    public DeviceWorkerProcess() => _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public async Task StartAsync(List<ControllerProfile> profiles, FFBConfiguration config, bool enableFfb, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await ConnectAsync(linked.Token);
        await SendAsync("start", new WorkerStart(profiles, config, enableFfb), linked.Token);
        _info = await _ready.Task.WaitAsync(linked.Token);
        Volatile.Write(ref _telemetry, _info.Telemetry);
    }
    public async Task<VJoyInspection> InspectAsync(CancellationToken token)
    {
        _isProbe = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await ConnectAsync(linked.Token);
        await SendAsync("probe", true, linked.Token);
        return await _inspection.Task.WaitAsync(linked.Token);
    }
    private async Task ConnectAsync(CancellationToken token)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("Executável atual não encontrado."))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        info.ArgumentList.Add("--device-worker"); info.ArgumentList.Add(_pipeName);
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        _process = Process.Start(info) ?? throw new IOException("Não foi possível iniciar o processo de dispositivos.");
        var connection = _pipe.WaitForConnectionAsync(token);
        if (await Task.WhenAny(connection, _process.WaitForExitAsync(token)) != connection)
            throw new IOException("O processo auxiliar encerrou antes de conectar.");
        await connection;
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, true);
        _ = ReadAsync();
    }
    private async Task ReadAsync()
    {
        try
        {
            using var reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, true);
            while (await reader.ReadLineAsync(_lifetime.Token) is { } line)
            {
                var message = JsonSerializer.Deserialize<WorkerMessage>(line, WorkerMessage.Json) ?? throw new InvalidDataException("Resposta inválida do worker.");
                switch (message.Kind)
                {
                    case "input-sample":
                        Volatile.Write(ref _latestInput, message.Read<InputSample>()); _captureReady.TrySetResult(); break;
                    case "probe-result": _inspection.TrySetResult(message.Read<VJoyInspection>()); break;
                    case "ready": _ready.TrySetResult(message.Read<WorkerReady>()); break;
                    case "telemetry": Volatile.Write(ref _telemetry, message.Read<WheelTelemetry>()); break;
                    case "error": throw new IOException(message.Read<string>());
                    case "test-result":
                        var result = message.Read<WorkerTestResult>();
                        if (_tests.TryRemove(result.Id, out var done))
                        {
                            if (result.Canceled) done.TrySetCanceled();
                            else if (result.Error != null) done.TrySetException(new IOException(result.Error));
                            else done.TrySetResult();
                        }
                        break;
                }
            }
            if (!_isProbe && !_isCapture) _ready.TrySetException(new IOException("Processo de dispositivos desconectado."));
            if (_isCapture) _captureReady.TrySetException(new IOException("Leitura de calibração desconectada."));
            if (_isProbe && !_inspection.Task.IsCompleted) _inspection.TrySetException(new IOException("Verificação vJoy desconectada."));
            _completion.TrySetResult();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        { _ready.TrySetCanceled(); _inspection.TrySetCanceled(); _captureReady.TrySetCanceled(); _completion.TrySetResult(); }
        catch (Exception ex) { if (!_isProbe && !_isCapture) _ready.TrySetException(ex); _captureReady.TrySetException(ex); _inspection.TrySetException(ex); _completion.TrySetException(ex); }
        finally { foreach (var done in _tests.Values) done.TrySetCanceled(); _tests.Clear(); }
    }
    private async Task SendAsync<T>(string kind, T value, CancellationToken token)
    {
        await _writeLock.WaitAsync(token);
        try
        {
            var writer = _writer ?? throw new IOException("Worker não conectado.");
            await writer.WriteLineAsync(WorkerMessage.Encode(kind, value).AsMemory(), token);
            await writer.FlushAsync(token);
        }
        finally { _writeLock.Release(); }
    }
    public void UpdateConfiguration(FFBConfiguration config) => _ = UpdateAsync(config);
    private async Task UpdateAsync(FFBConfiguration config)
    {
        try { await SendAsync("config", config, _lifetime.Token); }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) _completion.TrySetException(ex); }
    }
    public async Task TestAsync(MotorTest type)
    {
        var id = Guid.NewGuid();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tests[id] = done;
        try
        {
            await SendAsync("test", new WorkerTest(id, type), _lifetime.Token);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token);
        }
        finally { _tests.TryRemove(id, out _); }
    }
    public async Task StopAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                await SendAsync("stop", true, CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(500));
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        finally { await AbortAsync(); }
    }
    public async Task AbortAsync()
    {
        _lifetime.Cancel();
        _pipe.Dispose();
        foreach (var done in _tests.Values) done.TrySetCanceled();
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); _process = null; }
        }
        _completion.TrySetResult();
    }
}
