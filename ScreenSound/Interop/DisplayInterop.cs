using System.Runtime.InteropServices;

namespace ScreenSound.Interop;

/// <summary>
/// Display-mode P/Invoke: per-monitor resolution/refresh enumeration,
/// rotation (横屏/竖屏) and touch-digitizer presence.
/// </summary>
public static class DisplayInterop
{
    public const int ENUM_CURRENT_SETTINGS = -1;

    public const uint DM_DISPLAYORIENTATION = 0x00000080;
    public const uint DM_PELSWIDTH = 0x00100000;
    public const uint DM_PELSHEIGHT = 0x00200000;
    public const uint DM_DISPLAYFREQUENCY = 0x00400000;

    public const int DMDO_DEFAULT = 0;   // 横屏（原生方向）
    public const int DMDO_90 = 1;        // 竖屏 90°
    public const int DMDO_180 = 2;
    public const int DMDO_270 = 3;       // 竖屏 270°

    public const int DISP_CHANGE_SUCCESSFUL = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettingsW(
        string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsExW(
        string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd,
        uint dwFlags, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    /// <summary>SM_DIGITIZER — nonzero when the system has any touch/digitizer input.</summary>
    public const int SM_DIGITIZER = 94;

    /// <summary>Read the current mode (resolution / refresh / orientation) of one display.</summary>
    public static DEVMODE? GetCurrentMode(string deviceName)
    {
        var dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        return EnumDisplaySettingsW(deviceName, ENUM_CURRENT_SETTINGS, ref dm) ? dm : null;
    }

    /// <summary>
    /// Rotate one display. Rotation swaps the effective pixel dimensions —
    /// pass the already-swapped width/height for portrait modes.
    /// </summary>
    public static int SetOrientation(string deviceName, int orientation)
    {
        var dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        if (!EnumDisplaySettingsW(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
            return -1;

        // Keep current timings; only flip orientation + swapped geometry.
        var landscape = orientation == DMDO_DEFAULT || orientation == DMDO_180;
        dm.dmFields = (int)(DM_DISPLAYORIENTATION | DM_PELSWIDTH | DM_PELSHEIGHT);
        dm.dmDisplayOrientation = orientation;
        var (w, h) = ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight);
        if (!landscape && w > h) (dm.dmPelsWidth, dm.dmPelsHeight) = (h, w);
        if (landscape && w < h) (dm.dmPelsWidth, dm.dmPelsHeight) = (h, w);

        return ChangeDisplaySettingsExW(deviceName, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
    }
}
