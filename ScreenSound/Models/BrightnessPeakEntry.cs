using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace ScreenSound.Models;

/// <summary>
/// One editable "peak luminance" row shown under the brightness slider when
/// absolute sync is enabled. The user supplies the panel's rated nits because
/// no DDC/CI capability reports luminance — VCP 0x10 is a 0–100 percentage of
/// whatever the panel can do, which is exactly the information we're missing.
/// </summary>
public partial class BrightnessPeakEntry : ObservableObject
{
    /// <summary>Stable key: the monitor's GDI device name (\\.\DISPLAY1).</summary>
    public string DeviceName { get; init; } = string.Empty;

    private string _displayName = string.Empty;

    /// <summary>
    /// Display label, e.g. "DELL U2720Q (Primary)". Settable so a rename on the
    /// screen-details page updates these rows in place — a full rebuild would
    /// reload peak values from disk mid-edit.
    /// </summary>
    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    /// <summary>Raised after a user edit so the view-model can persist it.</summary>
    public Action? Changed { get; set; }

    private int _peakNits = DefaultPeakNits;

    /// <summary>Fallback used for panels the user hasn't calibrated yet.</summary>
    public const int DefaultPeakNits = 250;

    public int PeakNits
    {
        get => _peakNits;
        set
        {
            // Guard the range: a 0 or negative value would make the conversion
            // divide by zero / flip signs and drive the other panel to full.
            var clamped = Math.Clamp(value, 1, 5000);
            if (SetProperty(ref _peakNits, clamped))
                Changed?.Invoke();
        }
    }
}
