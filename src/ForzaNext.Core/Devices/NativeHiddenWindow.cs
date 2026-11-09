using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ForzaNext.Core.Devices;

public sealed class NativeHiddenWindow : IDisposable
{
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static readonly WndProc StaticWndProc = DefWindowProcW;
    private static readonly IntPtr WndProcPtr = Marshal.GetFunctionPointerForDelegate(StaticWndProc);

    private readonly IntPtr _hwnd;
    private readonly string _className;
    private bool _disposed;

    public IntPtr Handle => _hwnd;

    public NativeHiddenWindow(string title = "ForzaNext DirectInput Host")
    {
        _className = "FN_" + Guid.NewGuid().ToString("N");
        var hInst = GetModuleHandleW(null);

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = WndProcPtr,
            hInstance = hInst,
            lpszClassName = _className
        };

        if (RegisterClassExW(ref wc) == 0)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 0 && err != 1410) // 1410 = ERROR_CLASS_ALREADY_EXISTS
                throw new Win32Exception(err, "Falha ao registrar classe de janela DirectInput.");
        }

        // WS_POPUP = 0x80000000, WS_EX_TOOLWINDOW = 0x00000080
        _hwnd = CreateWindowExW(
            0x00000080,
            _className,
            title,
            0x80000000,
            0, 0, 1, 1,
            IntPtr.Zero,
            IntPtr.Zero,
            hInst,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Falha ao criar janela oculta para DirectInput.");
    }

    public static bool IsWindowOwnedByCurrentProcess(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hWnd, out uint pid);
        return pid == Environment.ProcessId;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
        }
        UnregisterClassW(_className, GetModuleHandleW(null));
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}

