using System.Collections.Concurrent;
using System.Diagnostics;
using ForzaNext.Core.Config;
using ForzaNext.Core.Devices;
using ForzaNext.Core.FFB;
using ForzaNext.Core.Virtual;
using ForzaNext.Core.Timing;

namespace ForzaNext.Core;

public sealed record FfbMonitor(long TotalPackets, double PacketsPerSecond, int ActiveEffects, string EffectTypes,
    bool Saturated, long SaturationEvents, long? LastPacketAgeMs, string TimerMode, long CoalescedPackets = 0, int PendingPackets = 0);
public sealed record WheelTelemetry(JoystickPositionV2 Position, double FrequencyHz, double MaxIntervalMs, int Force, string Status, long Sequence = 0, FfbMonitor? Ffb = null);
public enum MotorTest { Rack, Shift, Engine }

// A session owns native handles on one worker from acquisition through disposal.
public sealed class LocalWheelSession
{
    private readonly List<ControllerProfile> _profiles;
    private readonly IntPtr _window;
    private readonly bool _enableFfb;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FfbInbox _packets = new();
    private readonly ConcurrentQueue<(MotorTest Type, TaskCompletionSource Done)> _tests = new();
    private FFBConfiguration _configuration;
    private readonly object _requestsLock = new();
    private int _testPending;
    private long _totalPackets, _lastPacketAt;
    private readonly long[] _packetKinds = new long[32];
    private bool _acceptFfb = true;
    private string? _callbackError;
    private WheelTelemetry _telemetry = new(InputAggregator.NeutralPosition(), 0, 0, 0, "Inicializando");
    public Task Started => _started.Task;
    public Task Completion => _finished.Task;
    public WheelTelemetry Telemetry => Volatile.Read(ref _telemetry);
    public bool HasFfb { get; private set; }
    public Guid? FfbDevice { get; private set; }
    public List<ControllerProfile> ResolvedProfiles => ProfileManager.Snapshot(_profiles);

    public LocalWheelSession(List<ControllerProfile> profiles, IntPtr window, FFBConfiguration configuration, bool enableFfb = true)
    {
        _profiles = ProfileManager.Snapshot(profiles);
        _window = window;
        _enableFfb = enableFfb;
        _configuration = configuration;
        new Thread(Run) { IsBackground = true, Name = "ForzaNext device owner", Priority = ThreadPriority.AboveNormal }.Start();
    }
    public void UpdateConfiguration(FFBConfiguration config) => Volatile.Write(ref _configuration, config);
    public Task TestAsync(MotorTest type)
    {
        lock (_requestsLock)
        {
            if (_cancel.IsCancellationRequested || Completion.IsCompleted || !HasFfb)
                return Task.FromException(new InvalidOperationException("FFB não está ativo."));
            if (Interlocked.CompareExchange(ref _testPending, 1, 0) != 0)
                return Task.FromException(new InvalidOperationException("Aguarde o teste atual terminar."));
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _tests.Enqueue((type, done));
            return done.Task;
        }
    }
    public async Task StopAsync()
    {
        lock (_requestsLock)
            if (!Completion.IsCompleted) _cancel.Cancel();
        await Completion;
    }
    private void Receive(IntPtr pointer, FFBPType type)
    {
        if (_cancel.IsCancellationRequested || !Volatile.Read(ref _acceptFfb)) return;
        try
        {
            Interlocked.Increment(ref _totalPackets);
            Volatile.Write(ref _lastPacketAt, Environment.TickCount64);
            var packet = FfbPacket.CopyFromNative(pointer, type);
            if (packet == null) return;
            if ((uint)type < _packetKinds.Length) Interlocked.Increment(ref _packetKinds[(int)type]);
            if (!_packets.Enqueue(packet))
            {
                DiagnosticLog.Write("FFB backpressure", $"Pending={_packets.Count}; Coalesced={_packets.Coalesced}; " +
                    string.Join(", ", _packetKinds.Select((count, index) => $"{(FFBPType)index}={count}")));
                Volatile.Write(ref _callbackError, "Fila FFB excedeu o limite; sessão interrompida.");
                return;
            }
        }
        catch (Exception ex) { Volatile.Write(ref _callbackError, ex.Message); }
    }
    private void Run()
    {
        DirectInputDeviceManager? devices = null;
        VirtualWheelDriver? virtualWheel = null;
        DirectInputFFBBridge? bridge = null;
        TaskCompletionSource? testDone = null;
        MotorTest testType = default;
        long testStart = 0;
        bool reverse = false;
        string status = "Parado";
        Exception? failure = null;
        try
        {
            var token = _cancel.Token;
            token.ThrowIfCancellationRequested();
            if (_profiles.Count == 0) throw new InvalidOperationException("Mapeie pelo menos um dispositivo antes de iniciar.");
            Stage("Enumerando dispositivos físicos");
            devices = new DirectInputDeviceManager();
            var available = devices.EnumerateDevices().Where(d => !d.ProductName.Contains("vJoy", StringComparison.OrdinalIgnoreCase)).ToList();
            DirectInputDeviceManager.ResolveProfiles(_profiles, available);
            ProfileManager.Validate(_profiles);
            var candidates = _profiles.Where(p => available.Any(d => d.InstanceGuid == p.InstanceGuid && d.HasFFB)).ToList();
            var configuredTarget = _profiles.SingleOrDefault(p => p.FFBParameters != null);
            var target = configuredTarget != null
                ? candidates.FirstOrDefault(p => p.InstanceGuid == configuredTarget.InstanceGuid)
                : candidates.FirstOrDefault(p => p.Axes?.Any(a => a.Id == "Steering") == true);
            if (configuredTarget == null && target == null && candidates.Count == 1) target = candidates[0];
            if (configuredTarget == null && target == null && candidates.Count > 1)
                throw new InvalidOperationException("Há vários dispositivos FFB. Escolha o destino usando FFBParameters no perfil.");
            if (!_enableFfb) target = null;
            token.ThrowIfCancellationRequested();
            Stage("Inicializando vJoy 1");
            virtualWheel = new VirtualWheelDriver();
            virtualWheel.FfbPacketReceived += Receive;
            if (!virtualWheel.Initialize()) throw new InvalidOperationException(virtualWheel.LastError);
            virtualWheel.ValidateMappings(_profiles);
            SharpDX.DirectInput.Joystick? physical = null;
            foreach (var profile in _profiles)
            {
                token.ThrowIfCancellationRequested();
                Stage($"Adquirindo {profile.InstanceName} ({(profile == target ? "FFB exclusivo" : "somente entradas")})");
                var joystick = devices.AcquireDevice(profile.InstanceGuid, _window, profile == target,
                    step => Stage($"{profile.InstanceName}: {step}"))
                    ?? throw new InvalidOperationException($"Falha ao adquirir {profile.InstanceName}.");
                if (profile == target) physical = joystick;
            }
            if (physical != null && virtualWheel.HasFfb)
            {
                try
                {
                    Stage("Inicializando efeitos de Force Feedback");
                    bridge = new DirectInputFFBBridge(physical) { Config = _configuration };
                    if (!bridge.Initialize()) throw new InvalidOperationException("Atuador/força constante não suportado pelo volante.");
                    HasFfb = true;
                    FfbDevice = target!.InstanceGuid;
                }
                catch (Exception ex)
                {
                    bridge?.Dispose(); bridge = null;
                    DiagnosticLog.Write("FFB unavailable", ex.ToString());
                    status = $"Entradas ativas; FFB indisponível: {ex.Message}";
                }
            }
            else status = "Entradas ativas; sem destino FFB ou FFB desabilitado no vJoy 1.";
            Volatile.Write(ref _acceptFfb, HasFfb);
            var protocol = bridge == null ? null : new FfbProtocol(bridge);
            var aggregator = new InputAggregator(_profiles);
            var watch = Stopwatch.StartNew();
            long previous = watch.ElapsedTicks, next = previous, sampleStart = previous, publishAt = previous;
            double maxInterval = 0, measuredHz = 0, measuredInterval = 0;
            int samples = 0;
            long sampledPackets = 0, saturationEvents = 0;
            double packetRate = 0;
            bool wasSaturated = false;
            long sequence = 0;
            var period = Math.Max(1, Stopwatch.Frequency / 1000);
            using var loopTimer = new LoopTimer();
            DiagnosticLog.Write("Polling timer", loopTimer.Mode);
            var currentConfig = _configuration;
            var packetBatch = new List<FfbPacket>(256);
            token.ThrowIfCancellationRequested();
            if (HasFfb) status = "Entradas e FFB ativos";
            Volatile.Write(ref _telemetry, new(InputAggregator.NeutralPosition(), 0, 0, 0, status));
            _started.TrySetResult();
            while (!token.IsCancellationRequested)
            {
                if (Volatile.Read(ref _callbackError) is { } error) throw new InvalidOperationException(error);
                foreach (var profile in _profiles)
                    if (!devices.PollDevice(profile.InstanceGuid, aggregator.GetStateBuffer(profile.InstanceGuid)))
                        throw new IOException($"Leitura perdida: {profile.InstanceName}. Reconecte e inicie novamente.");
                sequence++;
                var position = aggregator.Aggregate();
                if (!virtualWheel.Feed(ref position)) throw new IOException("Falha ao atualizar vJoy. Confira o driver e inicie novamente.");
                if (bridge != null)
                {
                    bridge.NormalizedSteering = (position.AxisX - 1) / 32767.0;
                    var config = Volatile.Read(ref _configuration);
                    if (!ReferenceEquals(config, currentConfig))
                    {
                        bridge.Config = currentConfig = config;
                        protocol!.Refresh();
                    }
                    _packets.Drain(packetBatch);
                    if (testDone == null) protocol!.ProcessBatch(packetBatch);
                    if (testDone == null && _tests.TryDequeue(out var test))
                    {
                        protocol!.StopAll(); bridge.StopAll();
                        testDone = test.Done; testType = test.Type; reverse = false; testStart = watch.ElapsedMilliseconds;
                        StartTest(bridge, testType, false);
                    }
                    if (testDone != null)
                    {
                        long elapsed = watch.ElapsedMilliseconds - testStart;
                        if (testType == MotorTest.Rack && elapsed >= 250 && !reverse)
                        {
                            token.ThrowIfCancellationRequested();
                            StartTest(bridge, testType, true); reverse = true;
                        }
                        if (elapsed >= (testType == MotorTest.Rack ? 500 : 400))
                        {
                            bridge.Free(0); Interlocked.Exchange(ref _testPending, 0); testDone.TrySetResult(); testDone = null;
                        }
                    }
                }
                var now = watch.ElapsedTicks;
                bool saturated = bridge?.IsSaturated == true;
                if (saturated && !wasSaturated) saturationEvents++;
                wasSaturated = saturated;
                maxInterval = Math.Max(maxInterval, (now - previous) * 1000.0 / Stopwatch.Frequency);
                previous = now; samples++;
                if (now - sampleStart >= Stopwatch.Frequency)
                {
                    measuredHz = samples * (double)Stopwatch.Frequency / (now - sampleStart);
                    measuredInterval = maxInterval;
                    long total = Interlocked.Read(ref _totalPackets);
                    packetRate = (total - sampledPackets) * (double)Stopwatch.Frequency / (now - sampleStart);
                    sampledPackets = total;
                    samples = 0; maxInterval = 0; sampleStart = now;
                }
                if (now >= publishAt)
                {
                    var currentStatus = bridge?.Warning is { } warning ? "Entradas ativas; FFB parcial. " + warning : status;
                    long lastPacket = Volatile.Read(ref _lastPacketAt);
                    var monitor = new FfbMonitor(Interlocked.Read(ref _totalPackets), packetRate, bridge?.ActiveEffectCount ?? 0,
                        bridge?.ActiveEffectTypes ?? "Nenhum", saturated, saturationEvents, lastPacket == 0 ? null : Math.Max(0, Environment.TickCount64 - lastPacket), loopTimer.Mode, _packets.Coalesced, _packets.Count);
                    Volatile.Write(ref _telemetry, new(position, measuredHz, measuredInterval, bridge?.CurrentMagnitude ?? 0, currentStatus, sequence, monitor));
                    publishAt = now + Stopwatch.Frequency / 30;
                }
                next += period;
                long remaining = next - watch.ElapsedTicks;
                if (remaining > 0) loopTimer.WaitUntil(Stopwatch.GetTimestamp() + remaining, token);
                else next = watch.ElapsedTicks;
            }
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex; status = "Falha: " + ex.Message;
            DiagnosticLog.Write("Session", ex.ToString());
        }
        finally
        {
            // Complete cancellation first; no continuation can issue another motor command.
            _cancel.Cancel();
            HasFfb = false;
            testDone?.TrySetCanceled();
            lock (_requestsLock)
                while (_tests.TryDequeue(out var test)) test.Done.TrySetCanceled();
            if (virtualWheel != null) virtualWheel.FfbPacketReceived -= Receive;
            Cleanup(() =>
            {
                if (virtualWheel?.IsAcquired == true && !virtualWheel.Neutralize())
                    DiagnosticLog.Write("Neutralize", "Falha ao enviar relatório neutro.");
            }, "Neutralize cleanup");
            Cleanup(() => bridge?.Dispose(), "FFB cleanup");
            Cleanup(() => virtualWheel?.Dispose(), "Virtual cleanup");
            Cleanup(() => devices?.Dispose(), "Device cleanup");
            Volatile.Write(ref _telemetry, new(InputAggregator.NeutralPosition(), 0, 0, 0, status));
            if (failure != null) _started.TrySetException(failure);
            else _started.TrySetCanceled();
            lock (_requestsLock)
            {
                _finished.TrySetResult();
                _cancel.Dispose();
            }
        }
    }
    private void Stage(string status)
    {
        DiagnosticLog.Write("Worker startup", status);
        Volatile.Write(ref _telemetry, new(InputAggregator.NeutralPosition(), 0, 0, 0, status));
    }

    private static void Cleanup(Action action, string context)
    {
        try { action(); } catch (Exception ex) { DiagnosticLog.Write(context, ex.ToString()); }
    }
    private static void StartTest(DirectInputFFBBridge bridge, MotorTest type, bool reverse)
    {
        var state = new FfbEffectState
        {
            Report = new FfbEffReport { EffectType = type == MotorTest.Rack ? FFBEType.ET_CONST : FFBEType.ET_SINE,
                Duration = (ushort)(type == MotorTest.Rack ? 250 : 350), Gain = 255, Polar = 1, Direction = 64 },
            Constant = new FfbEffConstant { Magnitude = reverse ? -3500 : 3500 },
            Periodic = new FfbEffPeriod { Magnitude = (uint)(10000 * (type == MotorTest.Shift ? bridge.Config.GearShiftStrength : bridge.Config.EngineVibrationStrength)),
                Period = (uint)(50 / Math.Max(0.01, type == MotorTest.Shift ? bridge.Config.GearShiftFrequency : bridge.Config.EngineVibrationFrequency)) },
            Running = true, Iterations = 1
        };
        bridge.Apply(0, state, 255, true);
    }
}
