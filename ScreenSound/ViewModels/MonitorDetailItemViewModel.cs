using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Linq;
using ScreenSound.Interop;
using ScreenSound.Models;
using ScreenSound.Services;
using System.Collections.ObjectModel;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using System.Windows.Threading;

namespace ScreenSound.ViewModels;

/// <summary>Host callbacks the details page needs from the main view-model.</summary>
public sealed class MonitorDetailHost
{
    public required VcpService Vcp { get; init; }
    public required Action SaveSettings { get; init; }
    public required Func<string, int?> LoadPeakNits { get; init; }
    public required Action<string, int> StorePeakNits { get; init; }
    public required Func<string, List<int>?> LoadValidPresets { get; init; }
    public required Action<string, List<int>> StoreValidPresets { get; init; }
    public required Func<string, (int Kelvin, int EyeCare)> LoadColor { get; init; }
    public required Action<string, int, int> StoreColor { get; init; }

    /// <summary>
    /// Pushes a color change made on one card to every other card. The host
    /// decides whether it applies (color sync on) — the card just reports.
    /// </summary>
    public required Action<string, int, int> BroadcastColor { get; init; }

    /// <summary>
    /// All GDI device names of the SAME physical panel (a dual-link panel has
    /// two). Software color and rotation must reach every link, otherwise a
    /// control lands on the idle cable and silently does nothing when the
    /// panel's input source has switched. Returns at least the given name.
    /// </summary>
    public required Func<string, IReadOnlyList<string>> LinkedDeviceNames { get; init; }

    /// <summary>
    /// Persists a user-defined name / tag for one monitor. The host owns the
    /// settings dictionaries and re-applies them across all pages.
    /// </summary>
    public required Action<MonitorInfo, string, string> SaveIdentity { get; init; }
}

/// <summary>
/// One monitor's card on the screen-details page: read-only facts
/// (refresh rate / gamut / speakers / touch), the DDC/CI controls the panel
/// actually implements (contrast, color mode, RGB gain, sharpness, volume,
/// mute, input source), a software color layer (color temperature + low-blue
/// light via the gamma ramp) that also works on panels with no DDC at all,
/// and rotation. Brightness deliberately lives on the home page.
/// </summary>
public partial class MonitorDetailItemViewModel : ObservableObject
{
    public MonitorInfo Monitor { get; }

    private readonly VcpService _vcp;
    private readonly Action _saveSettings;
    private readonly Func<string, int?> _loadPeakNits;
    private readonly Action<string, int> _storePeakNits;
    private readonly Func<string, List<int>?> _loadValidPresets;
    private readonly Action<string, List<int>> _storeValidPresets;
    private readonly Func<string, (int, int)> _loadColor;
    private readonly Action<string, int, int> _storeColor;
    private readonly Action<string, int, int> _broadcastColor;
    private readonly Func<string, IReadOnlyList<string>> _linkedDeviceNames;
    private readonly Action<MonitorInfo, string, string> _saveIdentity;

    // ── Facts (read-only) ──
    public string ResolutionText { get; }
    public string RefreshRateText { get; private set; } = "";
    public string PanelName { get; private set; } = "";

    // ── Peak nits (mirrors the home-page brightness mapping, writable) ──
    [ObservableProperty]
    private int _peakNits;

    private bool _suppressPeakSave;

    // ── DDC controls ──
    [ObservableProperty]
    private double _contrast = -1;          // -1 = not loaded / unsupported

    [ObservableProperty]
    private double _speakerVolume = -1;

    public ObservableCollection<VcpOption> InputOptions { get; } = new();
    /// <summary>False when the panel exposes no switchable input list;
    /// the empty dropdown is then replaced by the OSD hint.</summary>
    [ObservableProperty]
    private bool _hasInputOptions;

    [ObservableProperty]
    private VcpOption? _selectedInput;

    public ObservableCollection<VcpOption> PresetOptions { get; } = new();

    [ObservableProperty]
    private VcpOption? _selectedPreset;

    /// <summary>Extra DDC rows discovered on this panel (RGB gain / sharpness / mute / scene).</summary>
    public ObservableCollection<VcpControlViewModel> ExtraControls { get; } = new();

    [ObservableProperty]
    private bool _ddcAvailable;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>True while the one-time capability probe runs (presets + extras).</summary>
    [ObservableProperty]
    private bool _isProbing;

    /// <summary>
    /// OSD features this panel refuses to expose over DDC (comma-joined,
    /// localized). Shown so "where is dynamic contrast?" has an answer that
    /// isn't "the app forgot it".
    /// </summary>
    [ObservableProperty]
    private string _hardwareOnlyText = "";

    public bool HasHardwareOnly => !string.IsNullOrEmpty(HardwareOnlyText);

    // ── Software color (gamma ramp, works without DDC) ──
    [ObservableProperty]
    private double _colorTemp = ColorGainService.NeutralKelvin;

    [ObservableProperty]
    private double _eyeCare;

    [ObservableProperty]
    private string _colorTempText = "";

    [ObservableProperty]
    private string _eyeCareText = "";

    private bool _suppressColor;
    private DispatcherTimer? _colorTimer;

    // ── Orientation ──
    [ObservableProperty]
    private bool _isPortrait;

    [ObservableProperty]
    private bool _isBusy;

    // ── Link diagnostics (connection type / bandwidth / power / USB) ──
    //
    // Raw data lives in _linkDiag; the five text rows are rebuilt from it so
    // a language switch can re-render them (see RefreshDisplayTexts).

    private LinkDiagnostics? _linkDiag;

    [ObservableProperty]
    private string _linkConnectionText = "";

    [ObservableProperty]
    private string _linkBandwidthText = "";

    [ObservableProperty]
    private string _linkPowerText = "";

    [ObservableProperty]
    private string _linkUsbText = "";

    [ObservableProperty]
    private string _linkWarningText = "";

    public bool HasLinkWarning => !string.IsNullOrEmpty(LinkWarningText);

    /// <summary>
    /// Pushes a fresh link snapshot (or null = unidentified) into the card.
    /// Called by the host after the cards are built and again whenever the
    /// display topology changes.
    /// </summary>
    public void UpdateLink(LinkDiagnostics? diag)
    {
        _linkDiag = diag;
        RebuildLinkTexts();
    }

    private void RebuildLinkTexts()
    {
        if (_linkDiag is null)
        {
            LinkConnectionText = LocalizationService.Get("LD_Tech_Unknown");
            LinkBandwidthText = "—";
            LinkPowerText = "—";
            LinkUsbText = "—";
            LinkWarningText = "";
        }
        else
        {
            var (connection, bandwidth, power, usb, warning) =
                LinkDiagnosticsService.BuildTexts(_linkDiag);
            LinkConnectionText = connection;
            LinkBandwidthText = bandwidth;
            LinkPowerText = power;
            LinkUsbText = usb;
            LinkWarningText = warning;
        }
        OnPropertyChanged(nameof(HasLinkWarning));
    }

    // Debounce for slider-driven DDC writes (dragging fires dozens of ticks).
    private readonly Dictionary<byte, DispatcherTimer> _debounce = new();
    private readonly Dictionary<byte, uint> _pending = new();

    /// <summary>Codes that make up the "hardware-only" list, in display order.</summary>
    private static readonly (byte Code, string Key)[] OptionalCodes =
    {
        (0xDC, "SD_Scenario"),      // display application / scene mode
        (0x87, "SD_Sharpness"),     // sharpness
        (0xE3, "SD_DynamicContrast"),   // vendor dynamic contrast (common on MSTAR/Realtek scalers)
        (0xD7, "SD_DynamicContrast"),
        (0xEB, "SD_DynamicContrast"),
        (0xF2, "SD_DynamicContrast"),
    };

    private readonly List<string> _missingKeys = new();

    public string DisplayName => Monitor.DisplayText;

    // ── Dual-link merging (one panel, two cables) ────────────────────────

    /// <summary>Hidden cards stay in the collection so broadcasts still reach both links.</summary>
    [ObservableProperty]
    private bool _isHidden;

    public bool IsCardVisible => !IsHidden;

    /// <summary>This card represents a panel wired over two different connectors.</summary>
    [ObservableProperty]
    private bool _isDualLink;

    [ObservableProperty]
    private string _dualLinkText = "";

    partial void OnIsHiddenChanged(bool value) => OnPropertyChanged(nameof(IsCardVisible));

    public void SetDualLink()
    {
        IsDualLink = true;
        DualLinkText = LocalizationService.Get("LD_DualLink_Note");
    }

    public void ResetDualLink()
    {
        IsDualLink = false;
        DualLinkText = "";
    }

    /// <summary>User label chip next to the name ("" when unset).</summary>
    public string Tag => Monitor.Tag;
    public bool HasTag => Monitor.HasTag;

    // ── Rename / tag editor ──────────────────────────────────────────────
    //
    // Inline rather than a dialog: the card header is exactly where the name
    // lives, and the app has no dialog infrastructure to reuse.

    [ObservableProperty]
    private bool _isEditingIdentity;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private string _editTag = string.Empty;

    [RelayCommand]
    private void BeginEditIdentity()
    {
        EditName = Monitor.CustomName;
        EditTag = Monitor.Tag;
        IsEditingIdentity = true;
    }

    [RelayCommand]
    private void SaveIdentityEdit()
    {
        _saveIdentity(Monitor, EditName, EditTag);
        IsEditingIdentity = false;
    }

    [RelayCommand]
    private void CancelIdentityEdit() => IsEditingIdentity = false;

    /// <summary>Re-reads name/tag after the host applied a saved identity.</summary>
    public void RefreshIdentity()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Tag));
        OnPropertyChanged(nameof(HasTag));
    }

    /// <summary>1-based card number shown in the header badge.</summary>
    public int MonitorNumber { get; }

    /// <summary>
    /// One-line facts digest ("2560x1600 · 60 Hz · sRGB · 有扬声器 · 不支持触控")
    /// — replaces the old 7-row label/value grid for a more compact card.
    /// </summary>
    public string SummaryText => string.Join("  ·  ", new[]
    {
        $"{ResolutionText} · {RefreshRateText}".Trim(' ', '·'),
        GamutText,
        string.IsNullOrEmpty(SpeakerEndpointName)
            ? LocalizationService.Get("SD_Speaker_Short_No")
            : LocalizationService.Get("SD_Speaker_Short_Yes"),
        HasTouchDigitizer
            ? LocalizationService.Get("SD_Touch_Short_Yes")
            : LocalizationService.Get("SD_Touch_No")
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public void RefreshDisplayText() => OnPropertyChanged(nameof(DisplayName));

    public MonitorDetailItemViewModel(
        MonitorInfo monitor,
        MonitorDetailsService.Details facts,
        MonitorDetailHost host,
        int monitorNumber = 0)
    {
        Monitor = monitor;
        _vcp = host.Vcp;
        _saveSettings = host.SaveSettings;
        _loadPeakNits = host.LoadPeakNits;
        _storePeakNits = host.StorePeakNits;
        _loadValidPresets = host.LoadValidPresets;
        _storeValidPresets = host.StoreValidPresets;
        _loadColor = host.LoadColor;
        _storeColor = host.StoreColor;
        _broadcastColor = host.BroadcastColor;
        _linkedDeviceNames = host.LinkedDeviceNames;
        _saveIdentity = host.SaveIdentity;
        MonitorNumber = monitorNumber;

        ResolutionText = $"{Monitor.Width}x{Monitor.Height}";
        RefreshRateText = facts.RefreshRate;
        PanelName = facts.PanelName;
        SpeakerEndpointName = facts.Speaker;
        HasTouchDigitizer = facts.Touch == "digitizer";
        GamutKey = facts.Gamut;

        _suppressPeakSave = true;
        _peakNits = Math.Clamp(_loadPeakNits(Monitor.DeviceName) ?? 250, 1, 5000);
        _suppressPeakSave = false;

        IsPortrait = Monitor.Height > Monitor.Width;

        var (kelvin, eyeCare) = _loadColor(Monitor.DeviceName);
        _suppressColor = true;
        _colorTemp = Math.Clamp(kelvin, ColorGainService.MinKelvin, ColorGainService.MaxKelvin);
        _eyeCare = Math.Clamp(eyeCare, 0, 100);
        ColorTempText = $"{(int)_colorTemp}K";
        EyeCareText = $"{(int)_eyeCare}%";
        _suppressColor = false;

        // Re-apply a saved adjustment so it survives an app restart (Windows
        // drops gamma ramps at logoff, not when this process exits).
        if ((int)_colorTemp != ColorGainService.NeutralKelvin || (int)_eyeCare > 0)
            ColorGainService.Apply(Monitor.DeviceName, (int)_colorTemp, (int)_eyeCare);
    }

    public string SpeakerEndpointName { get; }
    public bool HasTouchDigitizer { get; }
    public string GamutKey { get; }

    // Localised fact texts — recomputed on language switch via RefreshLocalizedTexts().
    public string GamutText => GamutKey switch
    {
        "wide" => LocalizationService.Get("SD_Gamut_Wide"),
        "sRGB" => LocalizationService.Get("SD_Gamut_Srgb"),
        _ => LocalizationService.Get("SD_Unknown")
    };

    public string SpeakerText => string.IsNullOrEmpty(SpeakerEndpointName)
        ? LocalizationService.Get("SD_Speaker_None")
        : string.Format(LocalizationService.Get("SD_Speaker_Has"), SpeakerEndpointName);

    public string TouchText => HasTouchDigitizer
        ? LocalizationService.Get("SD_Touch_Yes")
        : LocalizationService.Get("SD_Touch_No");

    public string RefreshDisplayTexts()
    {
        OnPropertyChanged(nameof(GamutText));
        OnPropertyChanged(nameof(SpeakerText));
        OnPropertyChanged(nameof(TouchText));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SummaryText));
        if (IsDualLink) DualLinkText = LocalizationService.Get("LD_DualLink_Note");
        RebuildHardwareOnlyText();
        RebuildLinkTexts();
        foreach (var c in ExtraControls) c.RefreshLocalizedText();
        return "";
    }

    /// <summary>Load DDC state + capabilities on a background thread. Called after refresh/language rebuild.</summary>
    public async Task LoadDdcStateAsync()
    {
        IsBusy = true;
        try
        {
            var caps = await Task.Run(() => _vcp.GetCapabilities(Monitor));

            // Contrast probe decides whether the card shows hardware controls at all.
            var contrast = await _vcp.GetVcpAsync(Monitor, MonitorInterop.VCP_CODE_CONTRAST);
            DdcAvailable = contrast.HasValue;
            if (contrast.HasValue) Contrast = contrast.Value.Current;

            // Input source: options come from the caps list; fall back to the
            // current value when the panel doesn't declare one.
            var curInput = await _vcp.GetVcpAsync(Monitor, MonitorInterop.VCP_CODE_INPUT_SELECT);
            var inputValues = VcpService.ParseCapValues(caps, MonitorInterop.VCP_CODE_INPUT_SELECT);
            if (curInput.HasValue && !inputValues.Contains(curInput.Value.Current))
                inputValues.Insert(0, curInput.Value.Current);

            InputOptions.Clear();
            foreach (var v in inputValues) InputOptions.Add(VcpOption.ForInput(v));
            SelectedInput = curInput.HasValue
                ? InputOptions.FirstOrDefault(o => o.Value == curInput.Value.Current)
                : null;

            // A single "current value" is not a switchable input list. Cheap
            // portable scalers ACK 0x60 but never change it (verified on the
            // HIV0160: every write was accepted, the read-back stayed at 0),
            // so a one-item dropdown would be a dead control. Clear it and
            // let the "use the OSD" hint show instead.
            if (InputOptions.Count < 2)
            {
                InputOptions.Clear();
                SelectedInput = null;
            }
            HasInputOptions = InputOptions.Count > 0;

            if (SpeakerVolume < 0)
            {
                var vol = await _vcp.GetVcpAsync(Monitor, MonitorInterop.VCP_CODE_SPEAKER_VOLUME);
                if (vol.HasValue) SpeakerVolume = vol.Value.Current;
            }

            if (!DdcAvailable) { RebuildHardwareOnlyText(); return; }

            await LoadColorModeAsync(caps);
            await LoadExtrasAsync(caps);
        }
        finally
        {
            IsBusy = false;
            IsProbing = false;
            StatusText = DdcAvailable ? "" : "noddc";
        }
    }

    /// <summary>
    /// Color preset / scene mode (VCP 0x14). The value list is validated once
    /// and cached: cheap panels advertise 13 presets but snap every unsupported
    /// one straight back, leaving a dropdown full of entries that do nothing.
    /// </summary>
    private async Task LoadColorModeAsync(string? caps)
    {
        var cur = await _vcp.GetVcpAsync(Monitor, MonitorInterop.VCP_CODE_COLOR_PRESET);
        if (!cur.HasValue)
        {
            _missingKeys.Add("SD_ColorMode");
            RebuildHardwareOnlyText();
            return;
        }

        var (current, max) = cur.Value;
        var valid = _loadValidPresets(Monitor.DeviceName)?.Select(v => (uint)v).ToList();
        if (valid == null || valid.Count == 0)
        {
            IsProbing = true;
            var fromCaps = VcpService.ParseCapValues(caps, MonitorInterop.VCP_CODE_COLOR_PRESET);
            if (fromCaps.Count > 0 && fromCaps.Contains(current))
            {
                valid = fromCaps;
            }
            else
            {
                valid = await _vcp.ProbeDiscreteValuesAsync(Monitor, MonitorInterop.VCP_CODE_COLOR_PRESET, current, max);
                if (valid.Count == 0) valid = new List<uint> { current };
                _storeValidPresets(Monitor.DeviceName, valid.Select(v => (int)v).ToList());
                _saveSettings();
            }
            IsProbing = false;
        }

        if (!valid.Contains(current)) valid.Insert(0, current);

        PresetOptions.Clear();
        foreach (var v in valid.Distinct().OrderBy(v => v))
            PresetOptions.Add(VcpOption.ForPreset(v));

        SelectedPreset = PresetOptions.FirstOrDefault(o => o.Value == current);
    }

    /// <summary>
    /// Discover the rest of the panel's adjustable codes. Every row is written
    /// back once before it is shown: a successful ACK is not proof of support
    /// (this machine's sharpness code answers 0/0 and ignores writes).
    /// </summary>
    private async Task LoadExtrasAsync(string? caps)
    {
        ExtraControls.Clear();

        var definitions = new (byte Code, string Key, bool Choice)[]
        {
            (0x16, "SD_GainR", false),
            (0x18, "SD_GainG", false),
            (0x1A, "SD_GainB", false),
            (0x87, "SD_Sharpness", false),
            (0x8D, "SD_Mute", true),
            (0xDC, "SD_Scenario", true),
            (0xE3, "SD_DynamicContrast", false),
            (0xD7, "SD_DynamicContrast", false),
            (0xEB, "SD_DynamicContrast", false),
            (0xF2, "SD_DynamicContrast", false),
        };

        foreach (var def in definitions)
        {
            var read = await _vcp.GetVcpAsync(Monitor, def.Code);
            if (!read.HasValue)
            {
                _missingKeys.Add(def.Key);
                continue;
            }

            var (current, max) = read.Value;
            if (max == 0)
            {
                // The panel answered, but with no range — only keep it when a
                // write actually takes effect.
                if (!await _vcp.ProbeWritableAsync(Monitor, def.Code, current, max))
                {
                    _missingKeys.Add(def.Key);
                    continue;
                }
                max = 100;
            }

            if (def.Choice)
            {
                List<VcpOption> options;
                if (def.Code == 0x8D)
                {
                    options = new List<VcpOption> { VcpOption.ForToggle(0), VcpOption.ForToggle(1) };
                }
                else
                {
                    var fromCaps = VcpService.ParseCapValues(caps, def.Code);
                    var values = fromCaps.Count > 0
                        ? fromCaps
                        : await _vcp.ProbeDiscreteValuesAsync(Monitor, def.Code, current, max);
                    if (values.Count == 0) values = new List<uint> { current };
                    if (!values.Contains(current)) values.Insert(0, current);
                    options = values.Distinct().OrderBy(v => v)
                        .Select(v => VcpOption.ForMode(def.Code, v)).ToList();
                }

                ExtraControls.Add(VcpControlViewModel.Choice(
                    Monitor, _vcp, def.Code, def.Key, options, current, OnControlStatus));
            }
            else
            {
                // RGB gain rows get a tint matching their channel name.
                Color? accent = def.Code switch
                {
                    0x16 => Color.FromRgb(0xD1, 0x34, 0x38), // red gain   (Fluent red-20)
                    0x18 => Color.FromRgb(0x13, 0xA1, 0x0E), // green gain (Fluent green)
                    0x1A => Color.FromRgb(0x00, 0x78, 0xD4), // blue gain  (Fluent blue)
                    _ => null
                };
                ExtraControls.Add(VcpControlViewModel.Slider(
                    Monitor, _vcp, def.Code, def.Key, current, max, OnControlStatus, accent));
            }
        }

        // Duplicate labels (four dynamic-contrast candidates) collapse to one entry.
        _missingKeys.RemoveAll(k => k == "SD_DynamicContrast" &&
                                    ExtraControls.Any(c => c.Code is 0xE3 or 0xD7 or 0xEB or 0xF2));

        RebuildHardwareOnlyText();
    }

    private bool CodeShown(byte code) => ExtraControls.Any(c => c.Code == code);

    private void OnControlStatus(string status)
    {
        if (!string.IsNullOrEmpty(status)) StatusText = status;
        else if (StatusText is "writefail" or "notsupported") StatusText = "";
    }

    /// <summary>"Dynamic contrast · Sharpness — only on the monitor's own buttons."</summary>
    private void RebuildHardwareOnlyText()
    {
        var names = _missingKeys.Distinct().ToList();

        // Digital brightness has no MCCS code at all: on every panel that
        // offers it, it lives in the OSD and nowhere else.
        if (DdcAvailable) names.Add("SD_DigitalBrightness");

        HardwareOnlyText = names.Count == 0
            ? ""
            : string.Format(LocalizationService.Get("SD_HardwareOnly"),
                string.Join("、", names.Select(LocalizationService.Get)));
        OnPropertyChanged(nameof(HasHardwareOnly));
    }

    partial void OnPeakNitsChanged(int value)
    {
        if (_suppressPeakSave) return;
        _storePeakNits(Monitor.DeviceName, value);
        _saveSettings();
    }

    partial void OnContrastChanged(double value)
    {
        if (!DdcAvailable || value < 0) return;
        DebouncedWrite(MonitorInterop.VCP_CODE_CONTRAST, (uint)value);
    }

    partial void OnSpeakerVolumeChanged(double value)
    {
        if (!DdcAvailable || value < 0) return;
        DebouncedWrite(MonitorInterop.VCP_CODE_SPEAKER_VOLUME, (uint)value);
    }

    private void DebouncedWrite(byte vcp, uint value)
    {
        if (!_debounce.TryGetValue(vcp, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            var captured = vcp;
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                _debounce.Remove(captured);
                var pending = _pending[captured];
                _pending.Remove(captured);
                _ = WriteVcpSafe(captured, pending);
            };
            _debounce[vcp] = timer;
        }

        _pending[vcp] = value;
        timer.Stop();
        timer.Start();
    }

    private async Task WriteVcpSafe(byte vcp, uint value)
    {
        var ok = await _vcp.SetVcpAsync(Monitor, vcp, value);
        StatusText = ok ? "" : "writefail";
    }

    partial void OnSelectedInputChanged(VcpOption? value)
    {
        if (value == null || !DdcAvailable) return;
        _ = ApplyInputAsync(value);
    }

    private async Task ApplyInputAsync(VcpOption option)
    {
        IsBusy = true;
        try
        {
            await _vcp.SetVcpAsync(Monitor, MonitorInterop.VCP_CODE_INPUT_SELECT, option.Value);
            // Input switches take a second or two to settle; re-read to confirm.
            await Task.Delay(1200);
            var back = await _vcp.GetVcpAsync(Monitor, MonitorInterop.VCP_CODE_INPUT_SELECT);
            StatusText = back.HasValue && back.Value.Current == option.Value ? "" : "inputverify";
        }
        finally { IsBusy = false; }
    }

    partial void OnSelectedPresetChanged(VcpOption? value)
    {
        if (value == null || !DdcAvailable) return;
        _ = ApplyPresetAsync(value);
    }

    private async Task ApplyPresetAsync(VcpOption option)
    {
        await _vcp.SetVcpAsync(Monitor, MonitorInterop.VCP_CODE_COLOR_PRESET, option.Value);
        await Task.Delay(320);
        var back = await _vcp.GetVcpAsync(Monitor, MonitorInterop.VCP_CODE_COLOR_PRESET);
        if (back.HasValue && back.Value.Current != option.Value)
        {
            // Snap-back: drop the dead entry so the list stays truthful.
            var keep = PresetOptions.Where(o => o.Value != option.Value).ToList();
            if (keep.Count > 0)
            {
                PresetOptions.Clear();
                foreach (var o in keep) PresetOptions.Add(o);
                _storeValidPresets(Monitor.DeviceName, keep.Select(o => (int)o.Value).ToList());
                _saveSettings();
            }
            SelectedPreset = PresetOptions.FirstOrDefault(o => o.Value == back.Value.Current);
            StatusText = "notsupported";
        }
    }

    // ── Software color (gamma) ──

    partial void OnColorTempChanged(double value)
    {
        ColorTempText = $"{(int)value}K";
        ScheduleColorWrite();
    }

    partial void OnEyeCareChanged(double value)
    {
        EyeCareText = $"{(int)value}%";
        ScheduleColorWrite();
    }

    private void ScheduleColorWrite()
    {
        if (_suppressColor) return;
        _colorTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _colorTimer.Tick -= ColorTimerTick;
        _colorTimer.Tick += ColorTimerTick;
        _colorTimer.Stop();
        _colorTimer.Start();
    }

    private void ColorTimerTick(object? sender, EventArgs e)
    {
        _colorTimer?.Stop();
        ApplyColor(store: true);
    }

    private void ApplyColor(bool store, bool broadcast = true)
    {
        var kelvin = (int)Math.Round(ColorTemp);
        var eye = (int)Math.Round(EyeCare);

        // A dual-link panel is two GDI devices sharing one physical screen.
        // Gamma ramps are per-device and independent, so write BOTH links:
        // if the panel has switched its input to the hidden cable, the ramp
        // on the visible card's link would otherwise go nowhere.
        var links = _linkedDeviceNames(Monitor.DeviceName);
        foreach (var name in links)
            ColorGainService.Apply(name, kelvin, eye);

        if (!store) return;
        foreach (var name in links)
            _storeColor(name, kelvin, eye);
        _saveSettings();

        // Color sync: mirror this card's values onto the other monitors. The
        // host ignores this while sync is off, so per-monitor editing still works.
        if (broadcast) _broadcastColor(Monitor.DeviceName, kelvin, eye);
    }

    /// <summary>
    /// Adopts another monitor's color setting (color sync pushed it here).
    /// Applied and persisted locally but not broadcast again — the source card
    /// already fanned out, and echoing back would ping-pong between cards.
    /// </summary>
    public void ApplySyncedColor(int kelvin, int eyeCare)
    {
        var clampedKelvin = Math.Clamp(kelvin, ColorGainService.MinKelvin, ColorGainService.MaxKelvin);
        var clampedEyeCare = Math.Clamp(eyeCare, 0, 100);

        _suppressColor = true;
        ColorTemp = clampedKelvin;
        EyeCare = clampedEyeCare;
        _suppressColor = false;

        ApplyColor(store: true, broadcast: false);
    }

    [RelayCommand]
    private void ResetColor()
    {
        _suppressColor = true;
        ColorTemp = ColorGainService.NeutralKelvin;
        EyeCare = 0;
        _suppressColor = false;
        ApplyColor(store: true);
    }

    [RelayCommand]
    private void ApplyLandscape() => ApplyOrientation(false);

    [RelayCommand]
    private void ApplyPortrait() => ApplyOrientation(true);

    private void ApplyOrientation(bool portrait)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            // Both links of a dual-link panel must be rotated in one CCD commit;
            // otherwise the panel shows the other input unaffected.
            var links = _linkedDeviceNames(Monitor.DeviceName);

            // Preferred: CCD route (same path as the Windows Settings app) —
            // works even when the driver exposes no portrait modes at all.
            var ccd = DisplayRotationService.TryRotate(links, portrait);

            // Fallback: classic GDI rotation (needs pre-swapped modes in the
            // driver's mode list, which many panels never provide). Try each
            // link; the one driving the visible input is the one that sticks.
            if (ccd == null)
            {
                var target = portrait ? DisplayInterop.DMDO_270 : DisplayInterop.DMDO_DEFAULT;
                ccd = false;
                foreach (var name in links)
                {
                    if (DisplayInterop.SetOrientation(name, target) == DisplayInterop.DISP_CHANGE_SUCCESSFUL)
                    { ccd = true; break; }
                }
            }

            IsPortrait = Monitor.Height > Monitor.Width;   // re-derive from actual geometry
            StatusText = ccd == true ? "" : "rotatefail";
        }
        finally { IsBusy = false; }
    }
}

/// <summary>One selectable value of a discrete VCP control (input source / color mode …).</summary>
public class VcpOption
{
    public uint Value { get; }
    public string Label { get; }

    private VcpOption(uint value, string label)
    {
        Value = value;
        Label = label;
    }

    private static readonly Dictionary<uint, string> InputNames = new()
    {
        [0x01] = "VGA",
        [0x03] = "DVI",
        [0x0F] = "DisplayPort",
        [0x11] = "HDMI 1",
        [0x12] = "HDMI 2",
        [0x20] = "USB-C",
        [0x21] = "USB-C",
    };

    /// <summary>MCCS 2.2 "Select Color Preset" values. Only the temperature
    /// entries are defined; everything else is a vendor mode.</summary>
    private static readonly Dictionary<uint, string> PresetNames = new()
    {
        [0x01] = "sRGB",
        [0x02] = "原生（无处理）",
        [0x03] = "4000K",
        [0x04] = "5000K",
        [0x05] = "6500K",
        [0x06] = "7500K",
        [0x07] = "8200K",
        [0x08] = "9300K",
        [0x09] = "10000K",
        [0x0A] = "11500K",
        [0x0B] = "用户 1",
        [0x0C] = "用户 2",
        [0x0D] = "用户 3",
    };

    public static VcpOption ForInput(uint v) =>
        new(v, InputNames.TryGetValue(v, out var n) ? n : $"输入源 0x{v:X2}");

    public static VcpOption ForPreset(uint v) =>
        new(v, PresetNames.TryGetValue(v, out var n) ? n : $"模式 {v}");

    public static VcpOption ForMode(byte code, uint v) =>
        code == 0x14 ? ForPreset(v) : new VcpOption(v, $"模式 {v}");

    public static VcpOption ForToggle(uint v) =>
        new(v, v == 0 ? LocalizationService.Get("SD_MuteOff") : LocalizationService.Get("SD_MuteOn"));

    public override string ToString() => Label;
}
