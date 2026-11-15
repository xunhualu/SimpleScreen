using System.Runtime.InteropServices;

namespace ScreenSound.Interop;

/// <summary>
/// Per-display gamma ramp (GDI). This is how the app adjusts color
/// temperature / low-blue-light in software for panels whose firmware
/// exposes no DDC control at all — the effect is per monitor because the
/// ramp is set on that display's device context.
/// </summary>
public static class GammaInterop
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDC(string? lpszDriver, string lpszDevice, IntPtr lpszPort, IntPtr lpdvmInit);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);
}
