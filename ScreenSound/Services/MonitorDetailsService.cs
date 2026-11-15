using Microsoft.Win32;
using ScreenSound.Interop;
using ScreenSound.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenSound.Services;

/// <summary>
/// Static per-monitor facts for the screen-details page: refresh rate,
/// color gamut (from EDID chromaticity), speaker presence (audio endpoints
/// named after the panel), touch support (system digitizer).
/// Peak nits come from the user's settings, not from hardware — DDC/CI
/// cannot report physical brightness.
/// </summary>
public static class MonitorDetailsService
{
    public class Details
    {
        public string RefreshRate = "";
        public string Gamut = "";
        public string Speaker = "";
        public string Touch = "";
        public string Connector = "";   // HDMI / USB-C / DP … best-effort from EDID
        public string PanelName = "";   // EDID descriptor model name, e.g. NE156QHM-NY1
    }

    /// <summary>Collect everything synchronously — cheap registry/enumeration calls, safe off-UI-thread.</summary>
    public static Dictionary<MonitorInfo, Details> GetAllDetails(
        List<MonitorInfo> monitors, List<string> audioOutputNames)
    {
        var internals = GetInternalPnpIds();
        var result = new Dictionary<MonitorInfo, Details>();
        var takenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: per-monitor facts + speaker matching by name.
        foreach (var m in monitors)
        {
            var d = BuildDetails(m, audioOutputNames);
            if (!string.IsNullOrEmpty(d.Speaker)) takenEndpoints.Add(d.Speaker);
            result[m] = d;
        }

        // Pass 2: a laptop's built-in panel has no audio endpoint named after
        // it — its speakers surface as "扬声器 (Realtek/C-Media...)" etc.
        // Whatever render endpoint no external monitor claimed is almost
        // surely it; prefer one whose name actually says "speaker".
        foreach (var m in monitors)
        {
            var pnpId = GetPnpId(m.DeviceName);
            var isInternal = internals.Contains(pnpId) ||
                             (pnpId.Length >= 3 && LaptopPanelPnpIds.Contains(pnpId[..3]));
            if (!isInternal) continue;
            var d = result[m];
            if (!string.IsNullOrEmpty(d.Speaker)) continue;
            var leftover = audioOutputNames
                .Where(n => !takenEndpoints.Contains(n))
                .OrderByDescending(n => n.Contains("speaker", StringComparison.OrdinalIgnoreCase)
                                      || n.Contains("扬声器", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (leftover != null)
            {
                d.Speaker = leftover;
                takenEndpoints.Add(leftover);
            }
        }

        return result;
    }

    /// <summary>Legacy single-monitor entry — kept for callers outside the details page.</summary>
    public static Details GetDetails(MonitorInfo monitor, IEnumerable<string> audioOutputNames) =>
        BuildDetails(monitor, audioOutputNames.ToList());

    private static Details BuildDetails(MonitorInfo monitor, List<string> audioOutputNames)
    {
        var d = new Details();

        // Refresh rate via the display-mode API.
        try
        {
            var dm = DisplayInterop.GetCurrentMode(monitor.DeviceName);
            if (dm.HasValue)
                d.RefreshRate = dm.Value.dmDisplayFrequency > 0
                    ? $"{dm.Value.dmDisplayFrequency} Hz" : "";
        }
        catch (Exception ex) { Debug.WriteLine($"[Details] mode failed: {ex.Message}"); }

        // EDID: gamut + panel model name.
        var edid = ReadEdid(monitor.DeviceName);
        if (edid != null)
        {
            d.Gamut = EstimateGamut(edid);
            d.PanelName = ParsePanelName(edid);
        }
        else
        {
            d.Gamut = "";
        }

        // Speakers: a real audio endpoint whose name mentions the panel
        // (e.g. "QHD (NVIDIA High Definition Audio)") implies the monitor
        // carries speakers / an audio path over its video cable.
        // Match against BOTH names: many cheap panels report "Generic PnP
        // Monitor" so FriendlyName falls back to "Monitor N" and never
        // matches the endpoint — the EDID product name ("QHD") is what the
        // audio stack actually uses, so try that too.
        var needles = new[] { monitor.FriendlyName.Trim(), d.PanelName.Trim() }
            .Where(n => n.Length >= 3).ToList();
        var found = audioOutputNames.FirstOrDefault(n =>
            needles.Any(nd => n.Contains(nd, StringComparison.OrdinalIgnoreCase)));
        d.Speaker = found != null ? found : "";

        // Touch: per-monitor detection isn't exposed by Windows; a global
        // digitizer is the honest closest signal.
        var digitizer = DisplayInterop.GetSystemMetrics(DisplayInterop.SM_DIGITIZER);
        d.Touch = digitizer != 0 ? "digitizer" : "none";

        return d;
    }

    /// <summary>
    /// Stable per-unit identity for a display, used as the settings key for
    /// user-defined names/tags. Prefers the monitor's device interface id
    /// ("MONITOR\HIV0160\{…}\0002"), which Windows keeps across reboots and
    /// distinguishes identical panels by connection; falls back to the PnP id,
    /// then to the GDI name, so a key always exists.
    /// Cached per GDI name — the enumeration underneath is not free and callers
    /// hit this on every monitor refresh.
    /// </summary>
    public static string GetIdentityKey(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return "";
        if (IdentityKeyCache.TryGetValue(deviceName, out var cached)) return cached;

        var key = "";
        try
        {
            for (uint i = 0; ; i++)
            {
                var adapter = new NativeMethods.DISPLAY_DEVICE();
                adapter.cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>();
                if (!NativeMethods.EnumDisplayDevices(null, i, ref adapter, 0)) break;
                if (!adapter.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase)) continue;

                var mon = new NativeMethods.DISPLAY_DEVICE();
                mon.cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>();
                if (NativeMethods.EnumDisplayDevices(adapter.DeviceName, 0, ref mon, 0))
                    key = mon.DeviceID?.Trim() ?? "";
                break;
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[Details] identity key failed: {ex.Message}"); }

        if (string.IsNullOrEmpty(key)) key = GetPnpId(deviceName);
        if (string.IsNullOrEmpty(key)) key = deviceName;

        IdentityKeyCache[deviceName] = key;
        return key;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> IdentityKeyCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>PnPID of the panel behind a GDI device name, e.g. "BOE0A07" from \\.\DISPLAY1.</summary>
    private static string GetPnpId(string deviceName)
    {
        try
        {
            for (uint i = 0; ; i++)
            {
                var adapter = new NativeMethods.DISPLAY_DEVICE();
                adapter.cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>();
                if (!NativeMethods.EnumDisplayDevices(null, i, ref adapter, 0)) break;
                if (!adapter.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase)) continue;

                for (uint j = 0; ; j++)
                {
                    var mon = new NativeMethods.DISPLAY_DEVICE();
                    mon.cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>();
                    if (!NativeMethods.EnumDisplayDevices(adapter.DeviceName, j, ref mon, 0)) break;
                    var parts = mon.DeviceID.Split('\\');
                    if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1])) return parts[1];
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[Details] pnpid failed: {ex.Message}"); }
        return "";
    }

    /// <summary>
    /// PnPIDs of built-in (laptop) panels. Two signals, either suffices:
    /// 1. WmiMonitorConnectionParams.VideoOutputTechnology in the embedded
    ///    range — but some firmwares misreport the internal panel (this ASUS
    ///    reports 11 = "external DisplayPort" while attached HIV portables
    ///    report 5/10!), so it cannot be trusted alone.
    /// 2. The EDID PnP vendor id being a known laptop-panel maker (BOE/AUO/
    ///    CMN/SDC...) — external desktop monitors virtually never use these.
    /// </summary>
    private static readonly HashSet<string> LaptopPanelPnpIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "AUO", "BOE", "CMN", "CMI", "LGD", "LG", "LPL", "SDC", "SEC", "SHP",
        "SCH", "HSD", "HAN", "IVO", "TMA", "NED", "JDI", "FTS", "SAM", "CPT", "CHI"
    };

    private static readonly HashSet<int> EmbeddedVotValues = new() { -2, 8, 12, 14 };

    private static HashSet<string> GetInternalPnpIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.UnionWith(LaptopPanelPnpIds);
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "root\\WMI", "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams");
            foreach (var o in searcher.Get())
            {
                var vot = Convert.ToInt32(o["VideoOutputTechnology"]);
                if (!EmbeddedVotValues.Contains(vot)) continue;
                var instance = Convert.ToString(o["InstanceName"]) ?? "";
                var seg = instance.Split('\\');
                if (seg.Length >= 2 && seg[0].Equals("DISPLAY", StringComparison.OrdinalIgnoreCase))
                    set.Add(seg[1]);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[Details] connection params failed: {ex.Message}"); }
        return set;
    }

    /// <summary>
    /// Map \\.\DISPLAYx → EDID. EnumDisplayDevices yields a DeviceID of
    /// "MONITOR\&lt;pnpid&gt;\{guid}\000n", but on many systems (this one included)
    /// the EDID lives under SYSTEM\CurrentControlSet\Enum\DISPLAY\&lt;pnpid&gt;\
    /// &lt;instance&gt;\Device Parameters — and Enum\MONITOR may not exist at all.
    /// So resolve the PnPID, then try every instance of that panel model
    /// (same model ⇒ same EDID content, so any instance is fine).
    /// </summary>
    private static byte[]? ReadEdid(string deviceName)
    {
        try
        {
            var pnpId = GetPnpId(deviceName);
            if (string.IsNullOrEmpty(pnpId)) return null;

            var baseKey = $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{pnpId}";
            using var modelKey = Registry.LocalMachine.OpenSubKey(baseKey);
            if (modelKey == null) return null;

            foreach (var instance in modelKey.GetSubKeyNames())
            {
                using var paramKey = modelKey.OpenSubKey($@"{instance}\Device Parameters");
                var edid = paramKey?.GetValue("EDID") as byte[];
                if (edid is { Length: >= 128 }) return edid;
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[Details] EDID read failed: {ex.Message}"); }
        return null;
    }

    /// <summary>
    /// Gamut estimate from the EDID chromaticity block (bytes 25–34):
    /// compare the panel's green/red primaries against the sRGB triangle —
    /// wide-gamut panels push green/red well beyond sRGB. "—" when the block
    /// is all zeros (many cheap portable panels never fill it in).
    /// </summary>
    private static string EstimateGamut(byte[] e)
    {
        // Byte 24 flags: bit4 = standard sRGB primaries.
        if (e.Length < 35) return "";
        if ((e[24] & 0x10) != 0) return "sRGB";

        double rx = ((e[27] << 2) | (e[25] >> 6)) / 1024.0;
        double ry = ((e[27] & 3) << 8 | e[28]) / 1024.0;
        double gx = ((e[29] & 0xF0) << 2 | e[30] >> 4) / 1024.0;
        double gy = ((e[29] & 0x0F) << 6 | e[31] >> 2) / 1024.0;
        if (rx == 0 && ry == 0 && gx == 0 && gy == 0) return "";   // unset block

        // sRGB primaries: R(0.640,0.330) G(0.300,0.600). Wide gamut green is
        // the giveaway (P3: 0.265/0.690, AdobeRGB: 0.21/0.71).
        var greenBeyondSrgb = gy > 0.62 || gx < 0.285;
        var redBeyondSrgb = rx > 0.645;
        return greenBeyondSrgb || redBeyondSrgb ? "wide" : "sRGB";
    }

    /// <summary>Panel model from EDID descriptor blocks 0xFC (name) / 0xFE (string).</summary>
    private static string ParsePanelName(byte[] e)
    {
        for (var i = 54; i + 17 <= 125; i += 18)
        {
            if (e[i] != 0 || e[i + 1] != 0) continue;
            var tag = e[i + 3];
            if (tag is 0xFC or 0xFE)
            {
                var text = new string(Array.ConvertAll(e[(i + 5)..(i + 18)], b => (char)b)).Trim('\0', '\r', '\n', ' ');
                if (text.Length > 1) return text;
            }
        }
        return "";
    }
}
