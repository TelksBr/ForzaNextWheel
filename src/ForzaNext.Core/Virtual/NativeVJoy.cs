using System;
using System.Runtime.InteropServices;

namespace ForzaNext.Core.Virtual;

public enum VjdStatus
{
    VJD_STAT_OWN = 0,
    VJD_STAT_FREE = 1,
    VJD_STAT_BUSY = 2,
    VJD_STAT_MISS = 3,
    VJD_STAT_UNKN = 4
}

public enum FFBPType
{
    PT_EFFREP = 1,
    PT_ENVREP = 2,
    PT_CONDREP = 3,
    PT_PRIDREP = 4,
    PT_CONSTREP = 5,
    PT_RAMPREP = 6,
    PT_CSTMREP = 7,
    PT_SMPLREP = 8,
    PT_EFOPREP = 10,
    PT_BLKFRREP = 11,
    PT_CTRLREP = 12,
    PT_GAINREP = 13,
    PT_SETCREP = 14,
    PT_NEWEFREP = 17,
    PT_BLKLDREP = 18,
    PT_POOLREP = 19
}

public enum FFBEType
{
    ET_NONE = 0,
    ET_CONST = 1,
    ET_RAMP = 2,
    ET_SQR = 3,
    ET_SINE = 4,
    ET_TRNGL = 5,
    ET_STUP = 6,
    ET_STDN = 7,
    ET_SPRNG = 8,
    ET_DMPR = 9,
    ET_INRT = 10,
    ET_FRCTN = 11,
    ET_CSTM = 12
}

public enum FFB_CTRL
{
    CTRL_ENACT = 1,
    CTRL_DISACT = 2,
    CTRL_STOPALL = 3,
    CTRL_DEVRST = 4,
    CTRL_DEVPAUSE = 5,
    CTRL_DEVCONT = 6
}

public enum FFBOP
{
    EFF_START = 1,
    EFF_SOLO = 2,
    EFF_STOP = 3
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FfbEffConstant
{
    public byte EffectBlockIndex;
    private byte _pad1, _pad2, _pad3;
    public int Magnitude; // LONG (32-bit: -10000 to 10000)
}

[StructLayout(LayoutKind.Explicit, Size = 28)]
public struct FfbEffReport
{
    // Brunner vJoy 2.2.2 SDK, not the legacy 24-byte vJoy 2.1.x layout.
    // The native decoder writes directions at offsets 24 and 26.
    [FieldOffset(0)] public byte EffectBlockIndex;
    [FieldOffset(4)] public FFBEType EffectType;
    [FieldOffset(8)] public ushort Duration;
    [FieldOffset(10)] public ushort TrigerRpt;
    [FieldOffset(12)] public ushort SamplePrd;
    [FieldOffset(14)] public ushort StartDelay;
    [FieldOffset(16)] public byte Gain;
    [FieldOffset(17)] public byte TrigerBtn;
    [FieldOffset(18)] public byte AxesEnabledDirection;
    [FieldOffset(20)] public int Polar; // BOOL (32-bit)
    [FieldOffset(24)] public ushort Direction; // union with DirX
    [FieldOffset(26)] public ushort DirY;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FfbEffOp
{
    public byte EffectBlockIndex;
    private byte _pad1, _pad2, _pad3;
    public FFBOP EffectOp;
    public byte LoopCount;
    private byte _pad4, _pad5, _pad6;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FfbEffCond
{
    public byte EffectBlockIndex;
    private byte _pad1, _pad2, _pad3;
    public int isY;               // BOOL (32-bit)
    public int CenterPointOffset; // LONG (32-bit)
    public int PosCoeff;          // LONG (32-bit)
    public int NegCoeff;          // LONG (32-bit)
    public uint PosSatur;         // DWORD (32-bit)
    public uint NegSatur;         // DWORD (32-bit)
    public int DeadBand;          // LONG (32-bit)
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FfbEffPeriod
{
    public byte EffectBlockIndex;
    private byte _pad1, _pad2, _pad3;
    public uint Magnitude; // DWORD (32-bit)
    public int Offset;     // LONG (32-bit)
    public uint Phase;     // DWORD (32-bit)
    public uint Period;    // DWORD (32-bit)
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FfbEffRamp
{
    public byte EffectBlockIndex;
    private byte _pad1, _pad2, _pad3;
    public int Start; // LONG (32-bit)
    public int End;   // LONG (32-bit)
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct JoystickPositionV2
{
    public byte bDevice;
    public int Throttle;
    public int Rudder;
    public int Aileron;
    public int AxisX;
    public int AxisY;
    public int AxisZ;
    public int AxisXRot;
    public int AxisYRot;
    public int AxisZRot;
    public int Slider;
    public int Dial;
    public int Wheel;
    public int AxisVX;
    public int AxisVY;
    public int AxisVZ;
    public int AxisVBRX;
    public int AxisVBRY;
    public int AxisVBRZ;
    public uint Buttons;       // 1-32; must precede POVs in JOYSTICK_POSITION_V2
    public uint bHats;
    public uint bHatsEx1;
    public uint bHatsEx2;
    public uint bHatsEx3;
    public uint ButtonsEx1;    // 33-64
    public uint ButtonsEx2;    // 65-96
    public uint ButtonsEx3;    // 97-128
}

public static class NativeVJoy
{
    private const string DllName = "vJoyInterface.dll";

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void FfbCallback(IntPtr data, IntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool vJoyEnabled();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern short GetvJoyVersion();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern VjdStatus GetVJDStatus(uint rID);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AcquireVJD(uint rID);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void RelinquishVJD(uint rID);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateVJD(uint rID, ref JoystickPositionV2 pData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ResetVJD(uint rID);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ResetAll();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetAxis(int value, uint rID, uint axis);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetBtn([MarshalAs(UnmanagedType.Bool)] bool value, uint rID, byte nBtn);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetDiscPov(int value, uint rID, byte nPov);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FfbRegisterGenCB(FfbCallback cb, IntPtr data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Ffb_h_DeviceID(IntPtr packet, ref int deviceId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Ffb_h_EBI(IntPtr packet, ref int index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsDeviceFfb(uint id);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DriverMatch(ref ushort dllVersion, ref ushort driverVersion);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVJDButtonNumber(uint id);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVJDContPovNumber(uint id);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVJDDiscPovNumber(uint id);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVJDAxisExist(uint id, uint axis);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Ffb_h_Eff_Envlp(IntPtr packet, ref FfbEffEnvelope envelope);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FfbStart(uint rID);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FfbStop(uint rID);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Ffb_h_Type(IntPtr Packet, ref FFBPType Type);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Ffb_h_Eff_Report(IntPtr Packet, ref FfbEffReport Effect);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "Ffb_h_Eff_Report")]
    internal static extern uint Ffb_h_Eff_ReportRaw(IntPtr packet, IntPtr output);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Ffb_h_Eff_Constant(IntPtr Packet, ref FfbEffConstant ConstantEffect);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Ffb_h_Eff_Cond(IntPtr Packet, ref FfbEffCond Condition);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Ffb_h_Eff_Period(IntPtr Packet, ref FfbEffPeriod Effect);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Ffb_h_Eff_Ramp(IntPtr Packet, ref FfbEffRamp RampEffect);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Ffb_h_EffOp(IntPtr Packet, ref FfbEffOp Operation);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Ffb_h_DevGain(IntPtr Packet, ref byte Gain);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Ffb_h_DevCtrl(IntPtr Packet, ref FFB_CTRL Control);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FfbEffEnvelope
{
    public byte EffectBlockIndex;
    public uint AttackLevel;
    public uint FadeLevel;
    public uint AttackTime;
    public uint FadeTime;
}
