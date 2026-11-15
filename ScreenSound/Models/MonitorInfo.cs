using System.Globalization;
using ScreenSound.Services;

namespace ScreenSound.Models;

public class MonitorInfo
{
    public IntPtr Handle { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Stable, per-unit identity used to key user-defined names and tags
    /// (e.g. "MONITOR\HIV0160\{4d36e96e-...}\0002"). Unlike the GDI device name
    /// (\\.\DISPLAY1) it survives reboots and display re-ordering, and unlike
    /// the bare PnP id it tells two physically identical monitors apart.
    /// Set by <see cref="MonitorService"/> from <see cref="MonitorDetailsService.GetIdentityKey"/>.
    /// </summary>
    public string IdentityKey { get; set; } = string.Empty;

    /// <summary>User-chosen name. Empty = use the hardware name.</summary>
    public string CustomName { get; set; } = string.Empty;

    /// <summary>Free-form user label (e.g. "工作屏" / "便携屏"), shown as a chip.</summary>
    public string Tag { get; set; } = string.Empty;

    public bool HasCustomName => !string.IsNullOrWhiteSpace(CustomName);
    public bool HasTag => !string.IsNullOrWhiteSpace(Tag);

    /// <summary>Custom name when set, otherwise the name Windows reports.</summary>
    public string BaseDisplayName => HasCustomName ? CustomName : FriendlyName;

    /// <summary>
    /// Built through the localisation tables so the "(Primary)" suffix follows
    /// the UI language. Width/Height are numbers — left culture-invariant so
    /// they never pick up localised digit grouping.
    /// </summary>
    public string DisplayText
    {
        get
        {
            var text = string.Format(CultureInfo.InvariantCulture, "{0} - {1}x{2}",
                BaseDisplayName, Width, Height);
            return IsPrimary ? $"{text} {LocalizationService.Get("Monitor_PrimarySuffix")}" : text;
        }
    }
}
