using System.Runtime.InteropServices;

namespace ForzaNext.Core.Virtual;

public sealed record FfbAbiResult(int ReportBytes, ushort DirectionX, ushort DirectionY, byte Gain, bool GuardIntact);

public static class FfbAbiValidation
{
    // A synthetic packet exercises the DLL parser only: no device is opened, no force is sent.
    // Use oversized unmanaged output with sentinels so an incompatible decoder cannot
    // overwrite a managed stack frame before its layout has been checked.
    public static FfbAbiResult Verify()
    {
        var data = Marshal.AllocHGlobal(20);
        var packet = Marshal.AllocHGlobal(16);
        var output = Marshal.AllocHGlobal(256);
        try
        {
            byte[] raw = { 0x11, 7, 1, 0x34, 0x12, 2, 0, 3, 0, 4, 0, 0x99, 5, 3, 0xff, 0x1f, 0x34, 0x12, 0, 0 };
            Marshal.Copy(raw, 0, data, raw.Length);
            Marshal.WriteInt32(packet, 28); // vJoy logical header size 8 + payload 20
            Marshal.WriteInt32(packet, 4, 0xB000F); // IOCTL_HID_WRITE_REPORT
            Marshal.WriteIntPtr(packet, 8, data);
            Marshal.Copy(Enumerable.Repeat((byte)0xA5, 256).ToArray(), 0, output, 256);
            if (NativeVJoy.Ffb_h_Eff_ReportRaw(packet, output) != 0)
                throw new InvalidDataException("O decodificador FFB do vJoy recusou o pacote de verificação.");
            var value = Marshal.PtrToStructure<FfbEffReport>(output);
            var guard = new byte[256 - 28]; Marshal.Copy(output + 28, guard, 0, guard.Length);
            bool intact = guard.All(b => b == 0xA5);
            if (!intact || value.EffectBlockIndex != 7 || value.EffectType != FFBEType.ET_CONST || value.Duration != 0x1234 ||
                value.TrigerRpt != 2 || value.SamplePrd != 3 || value.StartDelay != 4 || value.Gain != 0x99 ||
                value.TrigerBtn != 5 || value.AxesEnabledDirection != 3 || value.Polar != 0 || value.Direction != 0x1fff || value.DirY != 0x1234)
                throw new InvalidDataException("Layout FFB da DLL vJoy incompatível. Esta versão exige o SDK Brunner 2.2.2; nenhuma sessão foi adquirida.");
            return new(28, value.Direction, value.DirY, value.Gain, intact);
        }
        finally { Marshal.FreeHGlobal(output); Marshal.FreeHGlobal(packet); Marshal.FreeHGlobal(data); }
    }
}
