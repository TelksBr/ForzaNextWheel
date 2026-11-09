using ForzaNext.Core.Config;
using ForzaNext.Core.FFB;

namespace ForzaNext.Core;

internal interface IWheelWorker
{
    Task StartAsync(List<ControllerProfile> profiles, FFBConfiguration config, bool enableFfb, CancellationToken token);
    Task Completion { get; }
    WheelTelemetry Telemetry { get; }
    List<ControllerProfile> Profiles { get; }
    bool HasFfb { get; }
    Guid? FfbDevice { get; }
    void UpdateConfiguration(FFBConfiguration config);
    Task TestAsync(MotorTest type);
    Task StopAsync();
    Task AbortAsync();
}

// The UI never owns native device handles. A blocked driver can be terminated with its worker.
public sealed class WheelSession
{
    private readonly List<ControllerProfile> _profiles;
    private readonly Func<IWheelWorker> _factory;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _stallTimeout;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _run;
    private IWheelWorker? _worker;
    private FFBConfiguration _configuration;
    private List<ControllerProfile> _resolved;
    private bool _fallback;
    private string? _ffbFailure;
    public string? FfbFailure => Volatile.Read(ref _ffbFailure);
    private WheelTelemetry _last = new(InputAggregator.NeutralPosition(), 0, 0, 0, "Iniciando processo de dispositivos");
    public Task Started => _started.Task;
    public Task Completion => _finished.Task;
    public bool HasFfb => _worker?.HasFfb == true && !Completion.IsCompleted;
    public Guid? FfbDevice => _worker?.FfbDevice;
    public List<ControllerProfile> ResolvedProfiles => ProfileManager.Snapshot(_resolved);
    public WheelTelemetry Telemetry
    {
        get
        {
            var value = Volatile.Read(ref _worker)?.Telemetry ?? Volatile.Read(ref _last);
            if (!_fallback || Completion.IsCompleted) return value;
            string reason = "Somente entradas. " + FfbFailure;
            return value with { Status = value.Sequence > 0 ? reason : reason + " · " + value.Status, Force = 0 };
        }
    }

    public WheelSession(List<ControllerProfile> profiles, IntPtr window, FFBConfiguration configuration)
        : this(profiles, configuration, () => new DeviceWorkerProcess(), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)) { }

    internal WheelSession(List<ControllerProfile> profiles, FFBConfiguration configuration, Func<IWheelWorker> factory,
        TimeSpan startupTimeout, TimeSpan stallTimeout)
    {
        _profiles = ProfileManager.Snapshot(profiles);
        _resolved = _profiles;
        _configuration = configuration;
        _factory = factory;
        _startupTimeout = startupTimeout;
        _stallTimeout = stallTimeout;
        _run = Task.Run(RunAsync);
    }
    public void UpdateConfiguration(FFBConfiguration config)
    {
        Volatile.Write(ref _configuration, config);
        Volatile.Read(ref _worker)?.UpdateConfiguration(config);
    }
    public Task TestAsync(MotorTest type)
    {
        var worker = Volatile.Read(ref _worker);
        if (!HasFfb || worker == null || _cancel.IsCancellationRequested)
            return Task.FromException(new InvalidOperationException("FFB não está ativo."));
        return worker.TestAsync(type);
    }
    public async Task StopAsync()
    {
        _cancel.Cancel();
        await _run;
    }
    private async Task StartWorkerAsync(bool ffb)
    {
        _cancel.Token.ThrowIfCancellationRequested();
        var worker = _factory();
        Volatile.Write(ref _worker, worker);
        await worker.StartAsync(_profiles, Volatile.Read(ref _configuration), ffb, _cancel.Token)
            .WaitAsync(_startupTimeout, _cancel.Token);
    }
    private async Task RunAsync()
    {
        Exception? failure = null;
        try
        {
            if (_profiles.Count == 0) throw new InvalidOperationException("Mapeie pelo menos um dispositivo antes de iniciar.");
            try { await StartWorkerAsync(true); }
            catch (TimeoutException)
            {
                var blocked = Telemetry.Status;
                Volatile.Write(ref _ffbFailure, $"O volante não concluiu a abertura do FFB: {blocked}. Teste uma porta USB direta na placa-mãe, sem hub. Pare a sessão para tentar novamente.");
                DiagnosticLog.Write("Driver timeout", blocked);
                await AbortWorkerAsync();
                _cancel.Token.ThrowIfCancellationRequested();
                _fallback = true;
                await StartWorkerAsync(false);
            }
            _cancel.Token.ThrowIfCancellationRequested();
            _resolved = _worker!.Profiles;
            _started.TrySetResult();
            long lastSequence = _worker.Telemetry.Sequence;
            var progress = System.Diagnostics.Stopwatch.StartNew();
            while (!_cancel.IsCancellationRequested)
            {
                await Task.Delay(100, _cancel.Token);
                if (_worker.Completion.IsCompleted)
                {
                    await _worker.Completion;
                    throw new IOException("O processo de dispositivos encerrou. Confira os logs e inicie novamente.");
                }
                var telemetry = _worker.Telemetry;
                Volatile.Write(ref _last, telemetry);
                if (telemetry.Sequence != lastSequence) { lastSequence = telemetry.Sequence; progress.Restart(); }
                if (progress.Elapsed > _stallTimeout)
                    throw new TimeoutException("O driver deixou de responder. O processo de dispositivos será reiniciado ao iniciar novamente.");
            }
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex;
            DiagnosticLog.Write("Session supervisor", ex.ToString());
        }
        finally
        {
            if (Devices.HidHideService.IsInstalled)
            {
                try { Devices.HidHideService.UncloakSync(_profiles); } catch { }
            }
            var worker = Volatile.Read(ref _worker);
            if (worker != null)
            {
                try { await worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception ex) { DiagnosticLog.Write("Worker stop", ex.Message); await AbortWorkerAsync(); }
            }
            Volatile.Write(ref _worker, null);
            Volatile.Write(ref _last, new(InputAggregator.NeutralPosition(), 0, 0, 0,
                failure == null ? "Parado" : "Falha: " + failure.Message));
            if (failure != null) _started.TrySetException(failure);
            else _started.TrySetCanceled();
            _finished.TrySetResult();
        }
    }
    private async Task AbortWorkerAsync()
    {
        var worker = Volatile.Read(ref _worker);
        if (worker == null) return;
        try { await worker.AbortAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) { DiagnosticLog.Write("Abort worker", ex.ToString()); }
        Volatile.Write(ref _worker, null);
    }
}
