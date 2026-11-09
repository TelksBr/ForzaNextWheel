using SharpDX.DirectInput;
using ForzaNext.Core.Config;
using ForzaNext.Core.FFB;

namespace ForzaNext.Core.Devices;

public class ConnectedDeviceInfo
{
    public Guid InstanceGuid { get; set; }
    public Guid ProductGuid { get; set; }
    public string InstanceName { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public DeviceType DeviceType { get; set; }
    public bool HasFFB { get; set; }
}

public class PolledInputState
{
    public int[] Axes { get; } = new int[8];
    public bool[] Buttons { get; } = new bool[128];
    public int[] PointOfView { get; } = Enumerable.Repeat(-1, 4).ToArray();
    public bool IsValid { get; set; }

    public void Invalidate()
    {
        IsValid = false;
        Array.Clear(Axes);
        Array.Clear(Buttons);
        Array.Fill(PointOfView, -1);
    }
}

public sealed class DirectInputDeviceManager : IDisposable
{
    private readonly DirectInput _directInput = new();
    private readonly Dictionary<Guid, Joystick> _acquiredDevices = new();
    private readonly object _pollLock = new();
    private bool _disposed;
    private JoystickState _state = new();
    private readonly Dictionary<Guid, bool> _autoCenters = new();
    public string? LastError { get; private set; }

    public List<ConnectedDeviceInfo> EnumerateDevices()
    {
        lock (_pollLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = new List<ConnectedDeviceInfo>();
            foreach (var dev in _directInput.GetDevices())
            {
                if (dev.Type is DeviceType.Mouse or DeviceType.Keyboard) continue;
                bool hasFfb = false;
                try
                {
                    using var probe = new Joystick(_directInput, dev.InstanceGuid);
                    hasFfb = (probe.Capabilities.Flags & DeviceFlags.ForceFeedback) != 0;
                }
                catch (Exception ex) { DiagnosticLog.Write("Capabilities", ex.ToString()); }
                result.Add(new ConnectedDeviceInfo
                {
                    InstanceGuid = dev.InstanceGuid, ProductGuid = dev.ProductGuid,
                    InstanceName = dev.InstanceName, ProductName = dev.ProductName,
                    DeviceType = dev.Type, HasFFB = hasFfb
                });
            }
            return result;
        }
    }

    // Rebind only a unique product, never guess between two identical controllers.
    public static void ResolveProfiles(List<ControllerProfile> profiles, IReadOnlyList<ConnectedDeviceInfo> devices)
    {
        var used = profiles.Where(p => devices.Any(d => d.InstanceGuid == p.InstanceGuid)).Select(p => p.InstanceGuid).ToHashSet();
        foreach (var p in profiles)
        {
            var matches = devices.Where(d => d.InstanceGuid == p.InstanceGuid).ToList();
            if (matches.Count == 0 && p.ProductGuid != Guid.Empty)
                matches = devices.Where(d => d.ProductGuid == p.ProductGuid && !used.Contains(d.InstanceGuid)).ToList();
            if (matches.Count != 1)
                throw new InvalidOperationException($"Reconecte ou remapeie {p.InstanceName}: dispositivo ausente ou ambíguo.");
            var match = matches[0];
            p.InstanceGuid = match.InstanceGuid;
            p.ProductGuid = match.ProductGuid;
            p.InstanceName = match.InstanceName;
            p.ProductName = match.ProductName;
            used.Add(match.InstanceGuid);
        }
    }

    public Joystick? AcquireDevice(Guid instanceGuid, IntPtr windowHandle, bool isFfbDevice = false, Action<string>? stage = null)
    {
        lock (_pollLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_acquiredDevices.TryGetValue(instanceGuid, out var existing)) return existing;
            Joystick? joystick = null;
            try
            {
                stage?.Invoke("Criando dispositivo DirectInput");
                joystick = new Joystick(_directInput, instanceGuid);
                stage?.Invoke("Definindo acesso cooperativo");
                if (windowHandle != IntPtr.Zero)
                    joystick.SetCooperativeLevel(windowHandle, CooperativeLevel.Background |
                        (isFfbDevice ? CooperativeLevel.Exclusive : CooperativeLevel.NonExclusive));
                stage?.Invoke("Configurando faixas dos eixos");
                foreach (var obj in joystick.GetObjects())
                    if ((obj.ObjectId.Flags & DeviceObjectTypeFlags.Axis) != 0)
                        joystick.GetObjectPropertiesById(obj.ObjectId).Range = new InputRange(0, 65535);
                if (isFfbDevice)
                {
                    try
                    {
                        stage?.Invoke("Lendo centralização automática");
                        _autoCenters[instanceGuid] = joystick.Properties.AutoCenter;
                        stage?.Invoke("Desativando centralização automática");
                        joystick.Properties.AutoCenter = false;
                    }
                    catch (Exception ex) { DiagnosticLog.Write("AutoCenter unavailable", ex.Message); }
                }
                stage?.Invoke("Adquirindo acesso DirectInput");
                joystick.Acquire();
                stage?.Invoke("Dispositivo adquirido");
                _acquiredDevices.Add(instanceGuid, joystick);
                return joystick;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                DiagnosticLog.Write("Acquire", $"{instanceGuid}: {ex}");
                joystick?.Dispose();
                return null;
            }
        }
    }

    public bool PollDevice(Guid instanceGuid, PolledInputState output)
    {
        lock (_pollLock)
        {
            output.Invalidate();
            if (_disposed || !_acquiredDevices.TryGetValue(instanceGuid, out var device)) return false;
            try
            {
                device.Poll();
                device.GetCurrentState(ref _state);
                var state = _state;
                output.Axes[0] = state.X; output.Axes[1] = state.Y; output.Axes[2] = state.Z;
                output.Axes[3] = state.RotationX; output.Axes[4] = state.RotationY; output.Axes[5] = state.RotationZ;
                Array.Copy(state.Sliders, 0, output.Axes, 6, Math.Min(2, state.Sliders.Length));
                Array.Copy(state.Buttons, output.Buttons, Math.Min(128, state.Buttons.Length));
                Array.Copy(state.PointOfViewControllers, output.PointOfView, Math.Min(4, state.PointOfViewControllers.Length));
                output.IsValid = true;
                return true;
            }
            catch (SharpDX.SharpDXException sex) when (sex.ResultCode == ResultCode.InputLost || sex.ResultCode == ResultCode.NotAcquired)
            {
                DiagnosticLog.Write("Poll-Lost", $"{instanceGuid}: {sex.Message}. Tentando reaquisição...");
                FfbDebugLogger.Log("DINPUT-REACQUIRE", $"Tentando reaquisição para {instanceGuid}");
                try
                {
                    device.Acquire();
                    device.Poll();
                    device.GetCurrentState(ref _state);
                    var state = _state;
                    output.Axes[0] = state.X; output.Axes[1] = state.Y; output.Axes[2] = state.Z;
                    output.Axes[3] = state.RotationX; output.Axes[4] = state.RotationY; output.Axes[5] = state.RotationZ;
                    Array.Copy(state.Sliders, 0, output.Axes, 6, Math.Min(2, state.Sliders.Length));
                    Array.Copy(state.Buttons, output.Buttons, Math.Min(128, state.Buttons.Length));
                    Array.Copy(state.PointOfViewControllers, output.PointOfView, Math.Min(4, state.PointOfViewControllers.Length));
                    output.IsValid = true;
                    FfbDebugLogger.Log("DINPUT-REACQUIRE", $"Reaquisição bem-sucedida para {instanceGuid}");
                    return true;
                }
                catch (Exception retryEx)
                {
                    DiagnosticLog.Write("Poll-Failed", $"{instanceGuid}: Reaquisição falhou: {retryEx.Message}");
                    FfbDebugLogger.LogError("DINPUT-REACQUIRE", $"Reaquisição falhou para {instanceGuid}", retryEx);
                    output.Invalidate();
                    return false;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("Poll", $"{instanceGuid}: {ex.Message}");
                FfbDebugLogger.LogError("DINPUT-POLL", $"Erro ao ler dispositivo {instanceGuid}", ex);
                output.Invalidate();
                // The session stops on loss; no automatic reactivation of torque.
                return false;
            }
        }
    }

    public void ReleaseAll()
    {
        lock (_pollLock)
        {
            foreach (var (id, dev) in _acquiredDevices)
            {
                try { dev.Unacquire(); } catch (Exception ex) { DiagnosticLog.Write("Unacquire", ex.Message); }
                if (_autoCenters.TryGetValue(id, out var autoCenter))
                    try { dev.Properties.AutoCenter = autoCenter; } catch (Exception ex) { DiagnosticLog.Write("Restore AutoCenter", ex.Message); }
                try { dev.Dispose(); } catch (Exception ex) { DiagnosticLog.Write("Dispose device", ex.Message); }
            }
            _acquiredDevices.Clear();
            _autoCenters.Clear();
        }
    }

    public void Dispose()
    {
        lock (_pollLock)
        {
            if (_disposed) return;
            ReleaseAll();
            _directInput.Dispose();
            _disposed = true;
        }
    }
}
