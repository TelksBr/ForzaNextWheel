namespace ForzaNext.Core.Virtual;

public interface IVJoyDeviceLease
{
    bool IsActive { get; }
    Task<VJoySetupResult> StopAsync();
}

// Serializes startup and shutdown: even a close requested during startup releases the resulting lease.
public sealed class VJoyLifetime
{
    private readonly Func<Task<IVJoyDeviceLease>> _open;
    private readonly SemaphoreSlim _gate = new(1);
    private IVJoyDeviceLease? _lease;
    public bool IsActive => _lease?.IsActive == true;
    public VJoyLifetime() : this(async () => await VJoyDeviceLease.OpenAsync()) { }
    internal VJoyLifetime(Func<Task<IVJoyDeviceLease>> open) => _open = open;

    public async Task EnsureAsync(bool force = false)
    {
        await _gate.WaitAsync();
        try
        {
            if (!force && _lease?.IsActive == true) return;
            if (_lease != null)
            {
                var old = _lease;
                _lease = null;
                var result = await old.StopAsync();
                if (!result.Success) throw new IOException(result.Message);
            }
            _lease = await _open();
        }
        finally { _gate.Release(); }
    }

    public async Task<VJoySetupResult> StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_lease == null) return new(true, false, false, "O volante virtual não estava ativo nesta aplicação.");
            var lease = _lease;
            _lease = null;
            return await lease.StopAsync();
        }
        finally { _gate.Release(); }
    }
}
