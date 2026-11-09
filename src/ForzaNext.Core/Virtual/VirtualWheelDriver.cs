using System.Runtime.InteropServices;
using ForzaNext.Core.FFB;

namespace ForzaNext.Core.Virtual;

public sealed class VirtualWheelDriver : IDisposable
{
    private const uint DeviceId = 1;
    // Native code may retain this function pointer. Root it for the process lifetime.
    private static readonly NativeVJoy.FfbCallback Callback = OnNativeFfb;
    private static readonly object Ownership = new();
    private static VirtualWheelDriver? _owner;
    private bool _acquired;
    public bool IsAcquired => _acquired;
    public bool HasFfb { get; private set; }
    public string? LastError { get; private set; }
    public event Action<IntPtr, FFBPType>? FfbPacketReceived;

    public bool Initialize()
    {
        lock (Ownership)
        {
            if (_acquired) return true;
            try
            {
                if (_owner != null) throw new InvalidOperationException("vJoy já está em uso por esta aplicação.");
                LoadInstalledLibrary();
                FfbAbiValidation.Verify();
                if (!NativeVJoy.vJoyEnabled()) throw new InvalidOperationException("Driver vJoy não está habilitado.");
                ushort dll = 0, driver = 0;
                if (!NativeVJoy.DriverMatch(ref dll, ref driver))
                    throw new InvalidOperationException($"Versões incompatíveis do vJoy: DLL {dll:X}, driver {driver:X}.");
                var status = NativeVJoy.GetVJDStatus(DeviceId);
                if (status != VjdStatus.VJD_STAT_FREE)
                    throw new InvalidOperationException($"vJoy 1 indisponível: {status}. Feche outros feeders e confira o dispositivo.");
                if (!NativeVJoy.AcquireVJD(DeviceId)) throw new InvalidOperationException("Não foi possível adquirir vJoy 1.");
                _acquired = true;
                if (!NativeVJoy.ResetVJD(DeviceId)) throw new InvalidOperationException("Falha ao resetar vJoy 1.");
                HasFfb = NativeVJoy.IsDeviceFfb(DeviceId);
                NativeVJoy.FfbRegisterGenCB(Callback, IntPtr.Zero);
                try { NativeVJoy.FfbStart(DeviceId); } catch { }
                Volatile.Write(ref _owner, this);
                if (!Neutralize()) throw new InvalidOperationException("Falha ao enviar estado neutro ao vJoy.");
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                DiagnosticLog.Write("vJoy initialize", ex.ToString());
                Dispose();
                return false;
            }
        }
    }

    private static IntPtr _library;
    public static FfbAbiResult VerifyFfbAbi() { LoadInstalledLibrary(); return FfbAbiValidation.Verify(); }
    public static VJoyInspection Inspect()
    {
        try
        {
            LoadInstalledLibrary();
            bool enabled = NativeVJoy.vJoyEnabled();
            ushort dll = 0, driver = 0;
            bool matches = NativeVJoy.DriverMatch(ref dll, ref driver);
            var names = VJoyInspection.RecommendedAxes;
            uint[] usages = { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37 };
            var axes = names.Where((_, index) => NativeVJoy.GetVJDAxisExist(DeviceId, usages[index])).ToArray();
            return new(true, enabled, matches, NativeVJoy.GetVJDStatus(DeviceId),
                NativeVJoy.GetVJDButtonNumber(DeviceId), NativeVJoy.GetVJDContPovNumber(DeviceId), axes,
                NativeVJoy.IsDeviceFfb(DeviceId), null, dll, driver);
        }
        catch (DllNotFoundException) { return new(false, false, false, VjdStatus.VJD_STAT_MISS, 0, 0, Array.Empty<string>(), false, null); }
        catch (FileNotFoundException) { return new(false, false, false, VjdStatus.VJD_STAT_MISS, 0, 0, Array.Empty<string>(), false, null); }
        catch (Exception ex) { return new(true, false, false, VjdStatus.VJD_STAT_UNKN, 0, 0, Array.Empty<string>(), false, ex.Message); }
    }

    private static void LoadInstalledLibrary()
    {
        if (_library != IntPtr.Zero) return;
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy", "x64", "vJoyInterface.dll");
        var bundled = Path.Combine(AppContext.BaseDirectory, "vJoyInterface.dll");
        var path = File.Exists(installed) ? installed : bundled;
        _library = NativeLibrary.Load(path);
        NativeLibrary.SetDllImportResolver(typeof(NativeVJoy).Assembly,
            (name, _, _) => name == "vJoyInterface.dll" ? _library : IntPtr.Zero);
    }

    private static void OnNativeFfb(IntPtr data, IntPtr userData)
    {
        var owner = Volatile.Read(ref _owner);
        if (owner == null || data == IntPtr.Zero)
        {
            FfbDebugLogger.Log("VJOY-DROP-NULL", $"ownerNull={owner == null} dataZero={data == IntPtr.Zero}");
            return;
        }
        try
        {
            var type = FFBPType.PT_EFFREP;
            uint typeRes = NativeVJoy.Ffb_h_Type(data, ref type);
            if (typeRes != 0)
            {
                FfbDebugLogger.Log("VJOY-TYPE-ERR", $"Ffb_h_Type error {typeRes}");
                return;
            }
            int id = 0;
            // Only filter out if Ffb_h_DeviceID succeeded AND explicitly points to a different virtual device (> 1)
            if (NativeVJoy.Ffb_h_DeviceID(data, ref id) == 0 && id > 1 && id != DeviceId)
            {
                FfbDebugLogger.Log("VJOY-MISMATCH", $"id={id} type={type}");
                return;
            }
            owner.FfbPacketReceived?.Invoke(data, type);
        }
        catch (Exception ex)
        {
            FfbDebugLogger.Log("VJOY-EX", ex.ToString());
            DiagnosticLog.Write("FFB callback", ex.ToString());
        }
    }

    public bool Feed(ref JoystickPositionV2 state)
    {
        if (!_acquired) return false;
        state.bDevice = (byte)DeviceId;
        return NativeVJoy.UpdateVJD(DeviceId, ref state);
    }

    public void ValidateMappings(List<Config.ControllerProfile> profiles)
    {
        int required = RequiredButtonCount(profiles);
        int available = NativeVJoy.GetVJDButtonNumber(DeviceId);
        if (required > available)
            throw new InvalidOperationException($"vJoy 1 possui {available} botões; seu perfil exige {required}. Abra Configure vJoy, selecione o dispositivo 1 e habilite 128 botões (Buttons). Aplique e inicie novamente.");
        var usages = new Dictionary<string, uint> { ["Steering"] = 0x30, ["Throttle"] = 0x32, ["Brake"] = 0x33,
            ["Clutch"] = 0x34, ["Handbrake"] = 0x35, ["Slider0"] = 0x36, ["Slider1"] = 0x37 };
        foreach (var p in profiles)
        {
            foreach (var a in p.Axes ?? new())
                if (!NativeVJoy.GetVJDAxisExist(DeviceId, usages[a.Id]))
                    throw new InvalidOperationException($"Habilite o eixo virtual {a.Id} no vJoy 1.");
            if ((p.DPad != null || p.Buttons?.Any(b => b.Id is >= 32 and <= 35) == true) && NativeVJoy.GetVJDContPovNumber(DeviceId) < 1)
                throw new InvalidOperationException("Habilite um POV contínuo no vJoy 1 para usar o D-pad de oito direções.");
        }
    }

    public static int RequiredButtonCount(IEnumerable<Config.ControllerProfile> profiles) => profiles
        .SelectMany(p => p.Buttons ?? new()).Where(b => b.Id is not (>= 32 and <= 35))
        .Select(b => b.Id + 1).DefaultIfEmpty(0).Max();

    public bool Neutralize()
    {
        var neutral = InputAggregator.NeutralPosition();
        return Feed(ref neutral);
    }

    public void Dispose()
    {
        lock (Ownership)
        {
            if (ReferenceEquals(_owner, this)) Volatile.Write(ref _owner, null);
            if (!_acquired) return;
            try
            {
                if (!Neutralize()) DiagnosticLog.Write("vJoy stop", "Neutral report failed.");
            }
            finally
            {
                try { NativeVJoy.RelinquishVJD(DeviceId); }
                finally { _acquired = false; }
            }
        }
    }
}
