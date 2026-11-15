using ScreenSound.Interop;
using System.Runtime.InteropServices;

namespace ScreenSound.Services;

/// <summary>
/// Per-monitor link diagnostics — the "which cable is this screen on?" answer.
///
/// The only public Windows API that reveals how a monitor is physically
/// attached is QueryDisplayConfig: each active path carries a
/// <c>targetInfo.outputTechnology</c> (HDMI / DisplayPort / USB-C tunnel /
/// DisplayLink / internal panel / wireless …), and a source-name query maps
/// that path onto the classic GDI device name (<c>\\.\DISPLAY2</c>) the rest
/// of the app already uses.
///
/// Everything here is best-effort: the collection must never throw into the
/// UI pump. When the API isn't available the cards simply show "unidentified"
/// and the topology watchdog still works (it only needs EnumDisplayDevices).
/// </summary>
public sealed record LinkDiagnostics
{
    /// <summary>GDI device name, e.g. <c>\\.\DISPLAY2</c>.</summary>
    public required string DeviceName { get; init; }

    /// <summary>Raw DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY. Internal panels have the 0x80000000 bit set.</summary>
    public required int Technology { get; init; }

    /// <summary>Current refresh rate of the link, from the active path.</summary>
    public required double RefreshHz { get; init; }

    /// <summary>Target timing resolution driving the link.</summary>
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>
    /// Estimated data bandwidth of the current mode (Gbps). Pixels × refresh
    /// × 24 bit, plus a 12 % blanking allowance — the link's negotiated rate
    /// (HBR3, TMDS …) is not exposed by any public API, so this is a labelled
    /// estimate, never a measurement.
    /// </summary>
    public required double EstimatedGbps { get; init; }

    /// <summary>
    /// True when the same panel is reachable through more than one link at
    /// the same time (HDMI + Type-C on both, say). Windows then enumerates it
    /// as two DISPLAYn devices, and DDC/routing commands can land on the
    /// wrong instance.
    /// </summary>
    public required bool IsDuplicate { get; init; }

    /// <summary>GDI device names of the other links of the same panel.</summary>
    public required string DuplicatePeers { get; init; } = "";
}

public static class LinkDiagnosticsService
{
    // ── Topology snapshot (cheap, EnumDisplayDevices only) ────────────────

    /// <summary>
    /// Currently attached monitors as (GDI device name, EDID panel model).
    /// Used both for the duplicate-link detection and by the topology
    /// watchdog in MainWindowViewModel (signal-lost / duplicate-arrival
    /// alerts) — cheap enough to poll every few seconds.
    /// </summary>
    public static List<(string Device, string Model)> GetTopology()
    {
        var list = new List<(string Device, string Model)>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var dd = new NativeMethods.DISPLAY_DEVICE
                {
                    cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>()
                };
                if (!NativeMethods.EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & 1) == 0) continue;   // not attached to the desktop

                var mon = new NativeMethods.DISPLAY_DEVICE
                {
                    cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>()
                };
                var model = "?";
                if (NativeMethods.EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0) &&
                    !string.IsNullOrEmpty(mon.DeviceID))
                {
                    // MONITOR\<model>\{...} → <model>
                    var parts = mon.DeviceID.Split('\\');
                    if (parts.Length >= 2) model = parts[1];
                }

                list.Add((dd.DeviceName, model));
            }
        }
        catch
        {
            // enumeration must never throw into a timer tick
        }
        return list;
    }

    // ── Full diagnostics (QueryDisplayConfig) ────────────────────────────

    /// <summary>
    /// Link diagnostics for every active monitor, keyed by GDI device name.
    /// Monitors that couldn't be resolved simply don't appear — callers fall
    /// back to "unidentified" for them. The resolution comes from the
    /// monitors themselves (GDI current mode), the refresh rate and the
    /// connector technology from the active path.
    /// </summary>
    public static Dictionary<string, LinkDiagnostics> Collect(
        IReadOnlyList<Models.MonitorInfo> monitors)
    {
        var result = new Dictionary<string, LinkDiagnostics>(StringComparer.OrdinalIgnoreCase);
        var topology = GetTopology();

        try
        {
            if (NativeMethods.GetDisplayConfigBufferSizes(
                    NativeMethods.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
                return MarkDuplicates(result, topology);

            var paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[pathCount];

            // Opaque 64-byte slots — never read. The mode array only needs to
            // exist with the right element count; the OS writes well under 64
            // bytes per entry (verified by marker test on real hardware).
            var modes = new byte[Math.Max(modeCount, 1) * 64];

            var n = (uint)pathCount;
            var m = modeCount;
            if (NativeMethods.QueryDisplayConfig(
                    NativeMethods.QDC_ONLY_ACTIVE_PATHS,
                    ref n, paths, ref m, modes, IntPtr.Zero) != 0)
                return MarkDuplicates(result, topology);

            foreach (var path in paths)
            {
                if (path.targetInfo.targetAvailable == 0) continue;

                // Path → GDI device name (\\.\DISPLAYn)
                var src = new NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                src.header.type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                src.header.size = (uint)Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                src.header.adapterId = path.sourceInfo.adapterId;
                src.header.id = path.sourceInfo.id;
                if (NativeMethods.DisplayConfigGetDeviceInfo(ref src) != 0) continue;

                var refreshRate = path.targetInfo.refreshRate;
                var hz = refreshRate.Denominator > 0
                    ? (double)refreshRate.Numerator / refreshRate.Denominator
                    : 0;
                if (hz <= 0) hz = 60;

                var monitor = monitors.FirstOrDefault(
                    x => string.Equals(x.DeviceName, src.viewGdiDeviceName, StringComparison.OrdinalIgnoreCase));
                var width = monitor?.Width ?? 0;
                var height = monitor?.Height ?? 0;

                var gbps = width > 0 && height > 0
                    ? width * height * hz * 24 * 1.12 / 1e9
                    : 0;

                result[src.viewGdiDeviceName] = new LinkDiagnostics
                {
                    DeviceName = src.viewGdiDeviceName,
                    Technology = path.targetInfo.outputTechnology,
                    RefreshHz = hz,
                    Width = width,
                    Height = height,
                    EstimatedGbps = gbps,
                    IsDuplicate = false,
                    DuplicatePeers = ""
                };
            }
        }
        catch
        {
            // diagnostics degrade to "unidentified" — never break the UI
        }

        return MarkDuplicates(result, topology);
    }

    /// <summary>
    /// Flags every monitor whose EDID panel model is attached through more
    /// than one DISPLAYn device. Unknown models ("?") are ignored — two
    /// unidentified panels are not evidence of a shared panel.
    /// </summary>
    private static Dictionary<string, LinkDiagnostics> MarkDuplicates(
        Dictionary<string, LinkDiagnostics> dict,
        List<(string Device, string Model)> topology)
    {
        foreach (var group in topology
                     .Where(t => !string.IsNullOrEmpty(t.Model) && t.Model != "?")
                     .GroupBy(t => t.Model, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() < 2) continue;

            var names = group.Select(t => t.Device).ToList();
            foreach (var (device, _) in group)
            {
                if (!dict.TryGetValue(device, out var diag)) continue;
                var peers = string.Join(", ",
                    names.Where(n => !string.Equals(n, device, StringComparison.OrdinalIgnoreCase)));
                dict[device] = diag with { IsDuplicate = true, DuplicatePeers = peers };
            }
        }
        return dict;
    }

    // ── Localised card texts ─────────────────────────────────────────────

    /// <summary>
    /// The five rows the details card shows: connector, bandwidth estimate,
    /// power delivery, USB bottleneck relevance, and (possibly empty)
    /// warning line.
    /// </summary>
    public static (string Connection, string Bandwidth, string Power, string Usb, string Warning)
        BuildTexts(LinkDiagnostics d)
    {
        var tech = d.Technology;

        // 0x80000000 = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL (often OR'ed
        // onto LVDS / embedded DP). Anything negative except OTHER(-1) is an
        // internal panel.
        var internalPanel = (tech == 6 || tech == 11 ||
                             (tech < 0 && tech != -1));

        var connection = tech switch
        {
            5 => Get("LD_Tech_Hdmi"),
            4 => Get("LD_Tech_Dvi"),
            0 => Get("LD_Tech_Vga"),
            10 => Get("LD_Tech_Dp"),
            17 => Get("LD_Tech_DpTunnel"),
            15 or 16 => Get("LD_Tech_UsbVideo"),
            8 => Get("LD_Tech_Wireless"),
            _ when internalPanel => Get("LD_Tech_Internal"),
            _ => Get("LD_Tech_Unknown")
        };

        var bandwidth = d.Width > 0
            ? string.Format(Get("LD_Bandwidth_Format"), d.EstimatedGbps)
            : "—";

        var power = internalPanel ? Get("LD_Power_Internal") : Get("LD_Power_Unknown");

        var usb = tech switch
        {
            15 or 16 => Get("LD_Usb_VideoNote"),
            17 => Get("LD_Usb_TunnelNote"),
            10 => Get("LD_Usb_DpAltNote"),
            _ => Get("LD_Usb_Na")
        };

        string warning = "";

        // Conservative link caps as *data* rate: HDMI 2.0 ≈ 14.4 Gbps,
        // DisplayPort HBR3 ≈ 25.9 Gbps. If even these are exceeded the link
        // is being pushed past what its connector class commonly carries.
        if (tech == 5 && d.EstimatedGbps > 14.4)
            warning = string.Format(Get("LD_Warn_Bandwidth"), d.EstimatedGbps, "HDMI 2.0");
        else if ((tech == 10 || tech == 17) && d.EstimatedGbps > 25.9)
            warning = string.Format(Get("LD_Warn_Bandwidth"), d.EstimatedGbps, "DisplayPort HBR3");

        if (d.IsDuplicate)
        {
            var duplicate = string.Format(Get("LD_Warn_Duplicate"), d.DuplicatePeers);
            warning = string.IsNullOrEmpty(warning) ? duplicate : $"{duplicate}\n{warning}";
        }

        return (connection, bandwidth, power, usb, warning);
    }

    private static string Get(string key) => LocalizationService.Get(key);
}
