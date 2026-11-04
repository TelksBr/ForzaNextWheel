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

public enum FfbPacketType
{
    PT_EFFREP = 0x01,
    PT_ENVREP = 0x02,
    PT_CONDREP = 0x03,
    PT_PRIDREP = 0x04,
    PT_CONSTREP = 0x05,
    PT_RAMPREP = 0x06,
    PT_CSTMREP = 0x07,
    PT_SMPLREP = 0x08,
    PT_EFOPREP = 0x0A,
    PT_BLKLDREP = 0x0B,
    PT_POOLREP = 0x0C,
    PT_CTRLREP = 0x0D
}

[StructLayout(LayoutKind.Sequential)]
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
    public uint bHats;
    public uint bHatsEx1;
    public uint bHatsEx2;
    public uint bHatsEx3;
    public uint Buttons;       // 1-32
    public uint ButtonsEx1;    // 33-64
    public uint ButtonsEx2;    // 65-96
    public uint ButtonsEx3;    // 97-128
}

public static class NativeVJoy
{
    private const string DllName = "vJoyInterface.dll";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
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
    public static extern uint Ffb_h_Type(IntPtr Packet, out FfbPacketType Type);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Ffb_h_Packet(IntPtr Packet, out uint Type, out int DataSize, out IntPtr Data);
}
