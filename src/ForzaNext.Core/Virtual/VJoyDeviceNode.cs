using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace ForzaNext.Core.Virtual;

internal sealed record VJoyDeviceTarget(string InstanceId, ushort VendorId, ushort ProductId, byte ReportId);

// Resolve the HID collection by its input report ID, not by a guessed ROOT/HIDCLASS instance number.
internal static class VJoyDeviceNode
{
    private static string CachePath => Path.Combine(VJoySetupService.BackupDirectory, "device-one.json");
    internal const int ValueCapsSize = 72;
    internal const int ValueCapsReportIdOffset = 2;
    private const uint DisablePersist = 0x00000008;

    internal static bool IsSupportedIdentity(ushort vendor, ushort product, byte report) =>
        vendor == 0x1234 && product is 0xBEAD or 0x0FFB && report == 1;

    internal static bool IsSupportedInstance(string id) => Regex.IsMatch(id,
        @"^HID\\(?:VID_1234&PID_(?:BEAD|0FFB)(?:&[^\\]+)?|HIDCLASS)\\[^\\]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static VJoyDeviceTarget? Load()
    {
        if (!File.Exists(CachePath)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<VJoyDeviceTarget>(File.ReadAllText(CachePath));
            return value != null && !string.IsNullOrWhiteSpace(value.InstanceId) && IsSupportedIdentity(value.VendorId, value.ProductId, value.ReportId)
                && IsSupportedInstance(value.InstanceId) ? value : null;
        }
        catch (JsonException ex) { DiagnosticLog.Write("vJoy device identity", ex.Message); return null; }
    }

    public static void Save(VJoyDeviceTarget target)
    {
        Directory.CreateDirectory(VJoySetupService.BackupDirectory);
        var temporary = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(target)); File.Move(temporary, CachePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool Enable(VJoyDeviceTarget target)
    {
        if (!TryLocate(target, out uint node)) return false;
        uint status = 0, problem = 0;
        if (CM_Get_DevNode_Status(out status, out problem, node, 0) == 0 && problem != 22) return false;
        Check(CM_Enable_DevNode(node, 0), "Ativar vJoy 1");
        return true;
    }

    public static void Disable(VJoyDeviceTarget target)
    {
        if (!TryLocate(target, out uint node)) throw new IOException("O dispositivo vJoy 1 mudou de identidade; desativação não foi aplicada a outro dispositivo.");
        if (CM_Get_DevNode_Status(out _, out uint problem, node, 0) == 0 && problem == 22) return;
        Check(CM_Disable_DevNode(node, DisablePersist), "Desativar vJoy 1");
        DiagnosticLog.Write("vJoy lifecycle", "Dispositivo 1 desativado: " + target.InstanceId);
    }

    private static bool TryLocate(VJoyDeviceTarget target, out uint node)
    {
        node = 0;
        return IsSupportedIdentity(target.VendorId, target.ProductId, target.ReportId) && IsSupportedInstance(target.InstanceId)
            && CM_Locate_DevNodeW(out node, new StringBuilder(target.InstanceId), 0) == 0 && HasVJoyAncestor(node);
    }

    private static bool HasVJoyAncestor(uint node)
    {
        for (int depth = 0; depth < 12; depth++)
        {
            uint type = 0, length = 512;
            var buffer = new byte[length];
            if (CM_Get_DevNode_Registry_PropertyW(node, 5, out type, buffer, ref length, 0) == 0 &&
                Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(length, (uint)buffer.Length)).TrimEnd('\0')
                    .Equals("vjoy", StringComparison.OrdinalIgnoreCase)) return true;
            if (CM_Get_Parent(out var parent, node, 0) != 0) break;
            node = parent;
        }
        return false;
    }

    public static VJoyDeviceTarget FindDeviceOne()
    {
        HidD_GetHidGuid(out var hidGuid);
        IntPtr devices = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, 0x12);
        if (devices == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var matches = new List<VJoyDeviceTarget>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var entry = new DeviceInterface { Size = Marshal.SizeOf<DeviceInterface>() };
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref hidGuid, index, ref entry))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                GetDetailSize(devices, ref entry, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required < 8 || required > 65536) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    var info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                    if (!GetDetail(devices, ref entry, detail, required, out _, ref info)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    // Root-enumerated vJoy builds expose HID\HIDCLASS, without VID/PID in the path.
                    // Validate the owning driver and actual HID attributes instead of the path spelling.
                    if (!HasVJoyAncestor(info.DevInst)) continue;
                    using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;
                    var attributes = new HidAttributes { Size = Marshal.SizeOf<HidAttributes>() };
                    if (!HidD_GetAttributes(handle, ref attributes) || !IsSupportedIdentity(attributes.Vendor, attributes.Product, 1)) continue;
                    if (!HidD_GetPreparsedData(handle, out var parsed)) continue;
                    try
                    {
                        var caps = new HidCaps { Reserved = new ushort[17] };
                        if (HidP_GetCaps(parsed, ref caps) != 0x00110000 || caps.UsagePage != 1 || caps.Usage is not (4 or 5) || caps.InputValues == 0) continue;
                        ushort count = caps.InputValues;
                        IntPtr values = Marshal.AllocHGlobal(count * ValueCapsSize);
                        try
                        {
                            if (HidP_GetValueCaps(0, values, ref count, parsed) != 0x00110000) continue;
                            bool reportOne = false;
                            for (int i = 0; i < count; i++)
                                if (Marshal.ReadByte(values, i * ValueCapsSize + ValueCapsReportIdOffset) == 1) reportOne = true;
                            if (!reportOne) continue;
                            var id = new StringBuilder(1024);
                            Check(CM_Get_Device_IDW(info.DevInst, id, id.Capacity, 0), "Identificar vJoy 1");
                            if (IsSupportedInstance(id.ToString())) matches.Add(new(id.ToString(), attributes.Vendor, attributes.Product, 1));
                        }
                        finally { Marshal.FreeHGlobal(values); }
                    }
                    finally { HidD_FreePreparsedData(parsed); }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
        var unique = matches.DistinctBy(m => m.InstanceId).ToList();
        return unique.Count == 1 ? unique[0] : throw new IOException($"Não foi possível identificar a coleção HID do vJoy 1 ({unique.Count} correspondências). Consulte o diagnóstico da aplicação.");
    }

    private static void Check(uint code, string operation)
    {
        if (code != 0) throw new Win32Exception((int)CM_MapCrToWin32Err(code, 13), $"{operation}: Configuration Manager 0x{code:X}.");
    }

    [StructLayout(LayoutKind.Sequential)] internal struct DeviceInterface { public int Size; public Guid Guid; public uint Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceInfo { public int Size; public Guid Guid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct HidAttributes { public int Size; public ushort Vendor, Product, Version; }
    [StructLayout(LayoutKind.Sequential)] internal struct HidCaps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, ref HidCaps caps);
    [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int reportType, IntPtr caps, ref ushort length, IntPtr parsed);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr info, ref Guid guid, uint index, ref DeviceInterface entry);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDetailSize(IntPtr devices, ref DeviceInterface entry, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDetail(IntPtr devices, ref DeviceInterface entry, IntPtr detail, uint size, out uint required, ref DeviceInfo info);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint node, StringBuilder id, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_IDW(uint node, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, byte[] buffer, ref uint length, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Enable_DevNode(uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Disable_DevNode(uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_MapCrToWin32Err(uint code, uint fallback);
}
