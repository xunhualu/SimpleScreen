using CommunityToolkit.Mvvm.ComponentModel;

namespace ScreenSound.ViewModels;

/// <summary>
/// One editable row in the window-size-sync "screen diagonal" expander.
/// A row represents one <em>physical</em> panel, not one DISPLAYn: two cables
/// feeding the same panel share a model key, so they produce a single row.
/// The value is the diagonal inches printed on the screen's box; the app
/// combines it with the resolution Windows reports to derive real PPI.
/// </summary>
public partial class ScreenSizeEntryViewModel : ObservableObject
{
    private readonly Action<string, double> _storeInches;
    private readonly Action _saveSettings;
    private bool _suppress;

    /// <summary>Stable key shared by every cable of this panel (its model id).</summary>
    public string Model { get; }

    [ObservableProperty]
    private string _displayName = string.Empty;

    public string ResolutionText { get; }
    public bool IsPrimary { get; }

    [ObservableProperty]
    private double _inches;

    public ScreenSizeEntryViewModel(
        string model,
        string displayName,
        string resolutionText,
        bool isPrimary,
        double inches,
        Action<string, double> storeInches,
        Action saveSettings)
    {
        Model = model;
        _displayName = displayName;
        ResolutionText = resolutionText;
        IsPrimary = isPrimary;
        _storeInches = storeInches;
        _saveSettings = saveSettings;

        _suppress = true;
        _inches = inches;
        _suppress = false;
    }

    partial void OnInchesChanged(double value)
    {
        // 0 means "not filled in yet" — don't persist a sentinel as a real size.
        if (_suppress || value <= 0) return;
        _storeInches(Model, value);
        _saveSettings();
    }
}
