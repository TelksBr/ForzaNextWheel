using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ForzaNext.Core.Devices;

public sealed record UsbConnectionInfo(string Device, string InstanceId, int ExternalHubCount, string Route)
{
    public string Summary => ExternalHubCount == 0 ? $"{Device}: porta direta no controlador USB."
        : $"{Device}: {ExternalHubCount} hub(s) no caminho USB. Se o FFB não abrir, teste uma porta direta na placa-mãe.";
}

public static class UsbTopology
{
    public static IReadOnlyList<UsbConnectionInfo> Inspect()
    {
        var results = new List<UsbConnectionInfo>();
        // Only HID game-controller interfaces, never keyboards, mice or arbitrary USB data.
        Guid hid = new("745A17A0-74D3-11D0-B6FE-00A0C90F57DA");
        IntPtr devices = SetupDiGetClassDevsW(ref hid, null, IntPtr.Zero, 2);
        if (devices == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint i = 0; ; i++)
            {
                var info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(devices, i, ref info))
                { if (Marshal.GetLastWin32Error() == 259) break; throw new Win32Exception(Marshal.GetLastWin32Error()); }
                string id = GetId(info.Node);
                // Game HID class devices are children; require game-specific hardware usage.
                string hardware = Property(info.Node, 2);
                string compatible = Property(info.Node, 3);
                if (!hardware.Contains("HID_DEVICE_SYSTEM_GAME", StringComparison.OrdinalIgnoreCase) &&
                    !compatible.Contains("HID_DEVICE_SYSTEM_GAME", StringComparison.OrdinalIgnoreCase)) continue;
                var route = new List<string>(); int hubs = 0;
                uint node = info.Node;
                for (int depth = 0; depth < 20; depth++)
                {
                    string ancestorId = GetId(node);
                    string service = Property(node, 5).TrimEnd('\0');
                    if (ancestorId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                    {
                        route.Add(ancestorId);
                        if (!ancestorId.StartsWith("USB\\ROOT_HUB", StringComparison.OrdinalIgnoreCase) &&
                            service.StartsWith("USBHUB", StringComparison.OrdinalIgnoreCase)) hubs++;
                    }
                    if (CM_Get_Parent(out uint parent, node, 0) != 0) break;
                    node = parent;
                }
                if (route.Count == 0) continue; // vJoy and other software devices have no USB transport.
                string name = Property(info.Node, 13).TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(name)) name = Property(info.Node, 1).TrimEnd('\0');
                results.Add(new(name, id, hubs, string.Join(" → ", route)));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
        return results;
    }
    private static string GetId(uint node)
    { var text = new StringBuilder(1024); return CM_Get_Device_IDW(node, text, text.Capacity, 0) == 0 ? text.ToString() : ""; }
    private static string Property(uint node, uint property)
    {
        uint size = 4096; var buffer = new byte[size];
        return CM_Get_DevNode_Registry_PropertyW(node, property, out _, buffer, ref size, 0) == 0
            ? Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)) : "";
    }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public int Size; public Guid Class; public uint Node; public IntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(IntPtr devices, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_IDW(uint node, StringBuilder text, int length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, byte[] buffer, ref uint length, uint flags);
}
