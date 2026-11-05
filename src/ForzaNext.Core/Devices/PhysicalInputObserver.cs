using ForzaNext.Core.Config;

namespace ForzaNext.Core.Devices;

public sealed record InputCaptureRequest(Guid InstanceGuid, Guid ProductGuid);
public sealed record InputSample(Guid InstanceGuid, string Name, int[] Axes, long Sequence);

// Input-only acquisition: calibration never acquires vJoy or creates motor effects.
public sealed class PhysicalInputObserver
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private InputSample? _sample;
    public InputSample? Latest => Volatile.Read(ref _sample);
    public Task Completion => _done.Task;
    public string? Error { get; private set; }
    public PhysicalInputObserver(InputCaptureRequest request, IntPtr window)
    {
        new Thread(() =>
        {
            try
            {
                using var devices = new DirectInputDeviceManager();
                var available = devices.EnumerateDevices().Where(d => !d.ProductName.Contains("vJoy", StringComparison.OrdinalIgnoreCase)).ToList();
                var candidates = available.Where(d => d.InstanceGuid == request.InstanceGuid).ToList();
                if (candidates.Count == 0 && request.ProductGuid != Guid.Empty) candidates = available.Where(d => d.ProductGuid == request.ProductGuid).ToList();
                if (candidates.Count != 1) throw new IOException("Reconecte o dispositivo do eixo; a identificação está ausente ou ambígua.");
                var device = candidates[0];
                if (devices.AcquireDevice(device.InstanceGuid, window) == null) throw new IOException("Não foi possível abrir o dispositivo para calibração.");
                long sequence = 0; var input = new PolledInputState();
                while (!_cancel.IsCancellationRequested)
                {
                    if (!devices.PollDevice(device.InstanceGuid, input)) throw new IOException("Leitura perdida durante a calibração.");
                    Volatile.Write(ref _sample, new(device.InstanceGuid, device.InstanceName, (int[])input.Axes.Clone(), ++sequence));
                    _cancel.Token.WaitHandle.WaitOne(20);
                }
            }
            catch (Exception ex) { Error = ex.Message; }
            finally { _done.TrySetResult(); }
        }) { IsBackground = true, Name = "ForzaNext calibration inputs" }.Start();
    }
    public async Task StopAsync() { _cancel.Cancel(); await Completion; _cancel.Dispose(); }
}

public sealed class InputCaptureSession : IAsyncDisposable
{
    private readonly DeviceWorkerProcess _worker = new();
    private readonly CancellationTokenSource _cancel = new();
    public InputSample? Latest => _worker.LatestInput;
    public bool IsConnected => !_worker.Completion.IsCompleted && Latest != null;
    public async Task StartAsync(InputCaptureRequest request)
    {
        try { await _worker.StartCaptureAsync(request, _cancel.Token).WaitAsync(TimeSpan.FromSeconds(8), _cancel.Token); }
        catch { await _worker.AbortAsync(); throw; }
    }
    public async ValueTask DisposeAsync() { _cancel.Cancel(); await _worker.StopAsync(); _cancel.Dispose(); }
}
