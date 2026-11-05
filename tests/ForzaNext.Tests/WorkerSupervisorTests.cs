using ForzaNext.Core;
using ForzaNext.Core.Config;
using ForzaNext.Core.FFB;
using Xunit;

namespace ForzaNext.Tests;

public class WorkerSupervisorTests
{
    private static List<ControllerProfile> Profiles() => new()
    {
        new() { InstanceGuid = Guid.NewGuid(), ProductGuid = Guid.NewGuid(), InstanceName = "Wheel", ProductName = "Wheel" }
    };

    private sealed class Worker : IWheelWorker
    {
        private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => _complete.Task;
        public bool HangStart;
        public bool HangStop;
        public bool Aborted;
        public bool? FfbRequested;
        public bool HasFfb => FfbRequested == true;
        public Guid? FfbDevice => null;
        public List<ControllerProfile> Profiles { get; private set; } = new();
        public WheelTelemetry Telemetry { get; set; } = new(InputAggregator.NeutralPosition(), 0, 0, 0, "Adquirindo Wheel");
        public async Task StartAsync(List<ControllerProfile> profiles, FFBConfiguration config, bool enableFfb, CancellationToken token)
        {
            Profiles = profiles;
            FfbRequested = enableFfb;
            if (HangStart) await Task.Delay(Timeout.Infinite, token);
        }
        public void UpdateConfiguration(FFBConfiguration config) { }
        public Task TestAsync(MotorTest type) => Task.CompletedTask;
        public async Task StopAsync()
        {
            if (HangStop) await Task.Delay(Timeout.Infinite);
            _complete.TrySetResult();
        }
        public Task AbortAsync() { Aborted = true; _complete.TrySetResult(); return Task.CompletedTask; }
    }

    [Fact]
    public async Task BlockedFfbAcquireIsAbortedAndRetriedWithoutFfb()
    {
        var blocked = new Worker { HangStart = true };
        var fallback = new Worker { Telemetry = new(InputAggregator.NeutralPosition(), 200, 5, 0,
            "Entradas ativas; sem destino FFB ou FFB desabilitado no vJoy 1.", 1) };
        var workers = new Queue<IWheelWorker>(new[] { blocked, fallback });
        var session = new WheelSession(Profiles(), new(), () => workers.Dequeue(), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
        await session.Started.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(blocked.Aborted);
        Assert.True(blocked.FfbRequested);
        Assert.False(fallback.FfbRequested);
        Assert.False(session.HasFfb);
        Assert.Contains("Somente entradas", session.Telemetry.Status);
        Assert.Contains("Adquirindo Wheel", session.FfbFailure);
        Assert.Contains(session.FfbFailure!, session.Telemetry.Status);
        Assert.DoesNotContain("FFB desabilitado no vJoy", session.Telemetry.Status);
        Assert.Equal(0, session.Telemetry.Force);
        await session.StopAsync();
    }

    [Fact]
    public async Task CancellationDuringAcquireDoesNotLaunchFallbackOrWaitForNativeReturn()
    {
        var blocked = new Worker { HangStart = true };
        int launches = 0;
        var session = new WheelSession(Profiles(), new(), () => { launches++; return blocked; }, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
        while (blocked.FfbRequested == null) await Task.Delay(10);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, launches);
        Assert.True(session.Completion.IsCompleted);
        Assert.Equal(0u, session.Telemetry.Position.Buttons);
    }

    [Fact]
    public async Task BothStartupAttemptsAreBounded()
    {
        var first = new Worker { HangStart = true };
        var second = new Worker { HangStart = true };
        var workers = new Queue<IWheelWorker>(new[] { first, second });
        var session = new WheelSession(Profiles(), new(), () => workers.Dequeue(), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TimeoutException>(() => session.Started.WaitAsync(TimeSpan.FromSeconds(2)));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(first.Aborted);
        Assert.False(second.FfbRequested);
    }

    [Fact]
    public async Task FrozenFeedIsDetectedEvenIfWorkerKeepsPublishingHeartbeat()
    {
        var worker = new Worker();
        var session = new WheelSession(Profiles(), new(), () => worker, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(150));
        await session.Started;
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("driver deixou de responder", session.Telemetry.Status);
    }

    [Fact]
    public async Task BlockedNativeStopIsTerminatedInsteadOfBlockingWindowClose()
    {
        var worker = new Worker { HangStop = true };
        var session = new WheelSession(Profiles(), new(), () => worker, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        await session.Started;
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(worker.Aborted);
    }

    [Fact]
    public void IpcPreservesNativeReportFields()
    {
        var report = InputAggregator.NeutralPosition();
        report.Buttons = 0x80000000;
        report.AxisX = 30000;
        var json = WorkerMessage.Encode("telemetry", new WheelTelemetry(report, 980, 2.3, -2500, "Active", 42));
        var message = System.Text.Json.JsonSerializer.Deserialize<WorkerMessage>(json, WorkerMessage.Json)!;
        var copied = message.Read<WheelTelemetry>();
        Assert.Equal(report.Buttons, copied.Position.Buttons);
        Assert.Equal(30000, copied.Position.AxisX);
        Assert.Equal(uint.MaxValue, copied.Position.bHats);
        Assert.Equal(42, copied.Sequence);
    }
}
