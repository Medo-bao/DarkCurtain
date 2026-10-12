using System.Runtime.InteropServices;

namespace ScreenBlank;

internal static partial class NativeMethods
{
    internal const int WM_SYSCOMMAND = 0x0112;
    internal const int SC_MONITORPOWER = 0xF170;
    internal const int MonitorPowerOn = -1;
    internal const int HWND_BROADCAST = 0xFFFF;
    internal const int WM_HOTKEY = 0x0312;
    internal const int HWND_TOPMOST = -1;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    internal static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendNotifyMessageW", SetLastError = true)]
    internal static extern bool SendNotifyMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SetThreadExecutionState(IntPtr esFlags);

    internal static readonly IntPtr ES_CONTINUOUS = new(0x80000000);
    internal static readonly IntPtr ES_SYSTEM_REQUIRED = new(0x00000001);
    internal static readonly IntPtr ES_DISPLAY_REQUIRED = new(0x00000002);

    [DllImport("user32.dll")]
    internal static extern int ShowCursor(bool bShow);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    internal static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo);

    [DllImport("user32.dll")]
    internal static extern bool DestroyCursor(IntPtr cursor);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    internal static extern bool BitBlt(
        IntPtr hdcDest,
        int xDest,
        int yDest,
        int width,
        int height,
        IntPtr hdcSource,
        int xSource,
        int ySource,
        uint rasterOperation);

    [DllImport("user32.dll")]
    internal static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public DisplayDeviceStateFlags StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [Flags]
    internal enum DisplayDeviceStateFlags
    {
        AttachedToDesktop = 0x00000001,
        PrimaryDevice = 0x00000004,
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplayDevices(
        string? lpDevice,
        uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice,
        uint dwFlags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] lpRamp);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool SetDeviceGammaRamp(IntPtr hdc, IntPtr lpRamp);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, string? lpszOutput, IntPtr lpInitData);


    internal static void SetDisplayPower(int powerState)
    {
        if (!NotifyDisplayPower(new IntPtr(HWND_BROADCAST), powerState))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static bool NotifyDisplayPower(IntPtr window, int powerState)
    {
        // Other applications must not block our UI while handling a power notification.
        return SendNotifyMessage(window, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(powerState));
    }

}
