using System.Runtime.InteropServices;
using System.Text;

namespace ScreenSound.Interop;

/// <summary>
/// P/Invoke declarations for monitor brightness control:
///  - DDC/CI via dxva2.dll (VCP code 0x10 = brightness) for external monitors
///  - Physical monitor enumeration helpers
/// </summary>
public static class MonitorInterop
{
    // App-wide DDC/CI gate. VcpService (details page) and BrightnessService
    // (home page) both talk to the same panels, sometimes via two handles that
    // end at one physical panel's single I2C bus (dual-cable setups). Any two
    // concurrent transactions can collide and fail, so everything DDC goes
    // through this semaphore.
    public static readonly SemaphoreSlim DdcBus = new(1, 1);

    // ── Physical monitor enumeration (dxva2.dll) ────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor, ref uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor, uint dwPhysicalMonitorArraySize,
        [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool DestroyPhysicalMonitor(IntPtr hPhysicalMonitor);

    // ── VCP feature (DDC/CI) ────────────────────────────────────────────

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(
        IntPtr hMonitor, byte bVCPCode, ref LPMC_VCP_CODE_TYPE pvct,
        out uint pdwCurrentValue, out uint pdwMaximumValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetVCPFeature(
        IntPtr hMonitor, byte bVCPCode, uint dwNewValue);

    public enum LPMC_VCP_CODE_TYPE
    {
        MC_MOMENTARY = 0x0001,
        MC_SET_PARAMETER = 0x0002
    }

    /// <summary>VCP code for monitor brightness (DDC/CI standard).</summary>
    public const byte VCP_CODE_BRIGHTNESS = 0x10;
    /// <summary>VCP 0x12 — monitor contrast.</summary>
    public const byte VCP_CODE_CONTRAST = 0x12;
    /// <summary>VCP 0x14 — color preset (sRGB / warm / cool …). Often the only
    /// software hook for "eye-care / low blue light" on budget panels.</summary>
    public const byte VCP_CODE_COLOR_PRESET = 0x14;
    /// <summary>VCP 0x60 — active input source (HDMI / DP / USB-C …).</summary>
    public const byte VCP_CODE_INPUT_SELECT = 0x60;
    /// <summary>VCP 0x62 — audio speaker volume (monitor's own speakers).</summary>
    public const byte VCP_CODE_SPEAKER_VOLUME = 0x62;

    // ── Capabilities string (declared VCP codes + allowed values) ───────

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetCapabilitiesStringLength(IntPtr hMonitor, ref uint pdwCapabilitiesStringLengthInCharacters);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool CapabilitiesRequestAndCapabilitiesReply(
        IntPtr hMonitor, [Out] StringBuilder lpstrCapabilitiesString, uint dwCapabilitiesStringLengthInCharacters);

    /// <summary>
    /// Read the MCCS capabilities string (e.g. "…60(0F 11 21)14(05 04)…").
    /// Null on failure. 100 ms settle delay before the reply — the spec allows
    /// the panel to defer the answer.
    /// </summary>
    public static string? GetCapabilitiesString(IntPtr hMonitor)
    {
        try
        {
            uint len = 0;
            if (!GetCapabilitiesStringLength(hMonitor, ref len) || len < 2) return null;
            var sb = new StringBuilder((int)len + 1);
            Thread.Sleep(100);
            if (!CapabilitiesRequestAndCapabilitiesReply(hMonitor, sb, len)) return null;
            return sb.ToString();
        }
        catch { return null; }
    }
}
