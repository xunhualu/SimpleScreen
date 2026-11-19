using ScreenSound.Models;
using ScreenSound.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Globalization;
using System.IO;
using ScreenSound.Interop;

namespace ScreenSound.ViewModels;

public partial class MonitorMappingViewModel : ObservableObject
{
    public MonitorInfo Monitor { get; }

    [ObservableProperty]
    private AudioDeviceInfo? _selectedAudioDevice;

    [ObservableProperty]
    private int _volume = 100; // 0-100, maps to device master volume

    [ObservableProperty]
    private bool _hasDevice; // true when a real device is selected (not "No mapping")

    /// <summary>
    /// The user's persistent intent — the audio device ID this monitor should
    /// map to. Distinct from <see cref="SelectedAudioDevice"/>, which mirrors
    /// what's actually present in the dropdown right now.
    /// <para>
    /// When a device is unplugged, <see cref="SelectedAudioDevice"/> goes null
    /// (FirstOrDefault returns nothing for a missing ID), but DesiredAudioDeviceId
    /// stays — so re-plugging the same endpoint, or restarting the app while it
    /// happens to be present again, restores the mapping. Empty string means
    /// "(No mapping)" / explicitly cleared.
    /// </para>
    /// </summary>
    public string DesiredAudioDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Set by the owner while it applies a device to this card programmatically
    /// (startup resolve, hot-plug auto-follow). While true, assigning
    /// <see cref="SelectedAudioDevice"/> does NOT rewrite DesiredAudioDeviceId —
    /// that field is the persistent baseline the user chose, and an automatic
    /// headphone follow must not destroy it.
    /// </summary>
    internal bool SuppressIntentUpdate { get; set; }

    /// <summary>
    /// Suspended by the owner while it rebuilds the ComboBox items, so a
    /// transient Clear() doesn't look like the user blanking the primary card.
    /// </summary>
    internal bool SuspendBlankGuard { get; set; }

    private AudioDeviceInfo? _lastNonBlankDevice;

    public string DisplayName => Monitor.DisplayText;

    /// <summary>
    /// True for the primary display card. Only this card enforces the
    /// "always have a playback device" / headphone auto-follow policy —
    /// secondary panels keep the plain user-controlled mapping.
    /// </summary>
    public bool IsPrimaryDisplay => Monitor.IsPrimary;

    /// <summary>User label chip next to the name ("" when unset).</summary>
    public string Tag => Monitor.Tag;
    public bool HasTag => Monitor.HasTag;

    /// <summary>
    /// True for the "other cable" of one physical panel wired twice (HDMI +
    /// Type-C). The item stays in the collection because Windows still needs
    /// that DISPLAYn for window placement and gamma ramps — only its card is
    /// hidden so the panel is not listed twice.
    /// </summary>
    [ObservableProperty]
    private bool _isHidden;

    public bool IsCardVisible => !IsHidden;

    /// <summary>This visible card represents a panel wired over two cables.</summary>
    [ObservableProperty]
    private bool _isDualLink;

    partial void OnIsHiddenChanged(bool value) => OnPropertyChanged(nameof(IsCardVisible));

    /// <summary>
    /// Re-evaluates <see cref="DisplayName"/> / <see cref="Tag"/> — called after
    /// a language switch (MonitorInfo.DisplayText embeds a localised "(Primary)"
    /// suffix) and after the user renames the display.
    /// OnPropertyChanged is protected, so the refresh is exposed here.
    /// </summary>
    public void RefreshDisplayText()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Tag));
        OnPropertyChanged(nameof(HasTag));
    }

    public ObservableCollection<AudioDeviceInfo> AvailableDevices { get; }

    public event Action? MappingChanged;
    public event Action<MonitorMappingViewModel>? VolumeChanged;

    /// <summary>
    /// Raised only for a genuine user selection (not for programmatic
    /// refresh / auto-follow). The owner uses it to cancel an active
    /// headphone auto-follow so a manual choice always wins.
    /// </summary>
    public event Action<MonitorMappingViewModel>? DevicePickedByUser;

    public MonitorMappingViewModel(MonitorInfo monitor, ObservableCollection<AudioDeviceInfo> devices)
    {
        Monitor = monitor;
        AvailableDevices = devices;
    }

    partial void OnSelectedAudioDeviceChanged(AudioDeviceInfo? value)
    {
        var blank = value == null || string.IsNullOrEmpty(value.Id);

        // The primary display must always have a playback device. Refuse a
        // blank result (user picking "(No mapping)"): snap back to the last
        // real device instead of leaving the dropdown empty. Programmatic
        // refreshes set SuspendBlankGuard so their transient Clear() is not
        // mistaken for a user action, and SetDeviceProgrammatically uses
        // SuppressIntentUpdate to keep the saved base mapping untouched.
        if (IsPrimaryDisplay && blank && !SuspendBlankGuard && !SuppressIntentUpdate)
        {
            var fallback = _lastNonBlankDevice;
            if (fallback != null && !string.IsNullOrEmpty(fallback.Id))
            {
                SuppressIntentUpdate = true;
                try { SelectedAudioDevice = fallback; }
                finally { SuppressIntentUpdate = false; }
                return;
            }
        }

        HasDevice = !blank;
        if (!blank)
        {
            _lastNonBlankDevice = value;
            // Capture the user's intent — but only on a real user pick.
            // Programmatic headphone auto-follow must not rewrite the base.
            if (!SuppressIntentUpdate)
            {
                DesiredAudioDeviceId = value!.Id;
                DevicePickedByUser?.Invoke(this);
            }
        }
        MappingChanged?.Invoke();
    }

    /// <summary>
    /// Programmatically point this card at a device without treating it as a
    /// user choice — keeps the saved baseline mapping intact. Used for the
    /// primary display's never-blank guarantee and headphone auto-follow.
    /// </summary>
    public void SetDeviceProgrammatically(AudioDeviceInfo? device)
    {
        SuppressIntentUpdate = true;
        try { SelectedAudioDevice = device; }
        finally { SuppressIntentUpdate = false; }
    }

    /// <summary>
    /// True when no real device is selected (blank / "(No mapping)").
    /// </summary>
    public bool HasNoDevice => SelectedAudioDevice == null || string.IsNullOrEmpty(SelectedAudioDevice.Id);

    partial void OnVolumeChanged(int value)
    {
        VolumeChanged?.Invoke(this);
    }
}

public partial class SessionDisplayViewModel : ObservableObject
{
    [ObservableProperty] private string _processName = string.Empty;
    [ObservableProperty] private uint _processId;
    [ObservableProperty] private string _monitorName = string.Empty;
    [ObservableProperty] private string _audioDeviceName = string.Empty;
    [ObservableProperty] private ImageSource? _icon;

    /// <summary>
    /// True when this session is being routed by an explicit per-app pin rather
    /// than the monitor → device mapping. Drives the pin glyph in the row UI and
    /// the "Remove pin" entry in the right-click menu.
    /// </summary>
    [ObservableProperty] private bool _isOverridden;
}

/// <summary>
/// One entry in the Pinned Apps list. Wraps a process name + the device the
/// user has pinned it to. <see cref="AudioDeviceName"/> is resolved against the
/// current device list each time the overrides UI refreshes — if the pinned
/// device is currently unplugged, name resolution falls back to a placeholder
/// while the underlying ID stays intact, mirroring the monitor-mapping behavior.
/// </summary>
public partial class AppOverrideViewModel : ObservableObject
{
    [ObservableProperty] private string _processName = string.Empty;
    [ObservableProperty] private string _audioDeviceId = string.Empty;
    [ObservableProperty] private string _audioDeviceName = string.Empty;

    /// <summary>
    /// App icon shown in the Pinned page. Captured from the originating session
    /// at pin-creation time, or back-filled by <c>MainWindowViewModel</c> when a
    /// pin loaded from disk matches a live session on the next session update.
    /// Null while the pinned app isn't running — the UI falls back to a pin glyph.
    /// </summary>
    [ObservableProperty] private ImageSource? _icon;
}

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly MonitorService _monitorService;
    private readonly AudioDeviceService _audioDeviceService;
    private readonly VcpService _vcpService = new();

    /// <summary>Per-monitor cards for the screen-details page (第 5 页).</summary>
    public ObservableCollection<MonitorDetailItemViewModel> MonitorDetails { get; } = new();
    private readonly AudioSessionService _sessionService;
    private readonly AudioRouterService _routerService;
    private readonly RoutingEngine _routingEngine;
    private readonly SettingsService _settingsService;
    private readonly UpdateService _updateService;
    private readonly AudioDeviceNotifier _deviceNotifier;
    private readonly BrightnessService _brightnessService;
    private readonly DispatcherTimer _deviceRefreshTimer;
    private readonly DispatcherTimer _brightnessPollTimer;
    private readonly DispatcherTimer _brightnessApplyTimer;
    private readonly DispatcherTimer _linkWatchTimer;
    private readonly Dispatcher _dispatcher;

    // ── Brightness sync state ───────────────────────────────────────────
    //
    // Brightness sync keeps every controllable monitor at one shared value.
    // Two directions of flow:
    //   • user drags the in-app slider  → debounce 250 ms → push to all panels
    //   • user presses the monitor's own physical buttons / OSD
    //                                   → 1 s poll notices the odd-one-out
    //                                   → pull the other panels to that value
    // DDC/CI writes take 50–200 ms each, so polls are suspended for a short
    // settle window after every apply to avoid reading stale values back and
    // bouncing between two numbers.
    private List<BrightnessService.BrightnessMonitor> _brightnessMonitors = new();
    private DateTime _brightnessSettleUntil = DateTime.MinValue;
    private bool _applyingBrightness;
    private int _lastSyncedBrightness = -1;
    private bool _brightnessReady;
    private List<(BrightnessService.BrightnessMonitor Bm, int Value)>? _lastBrightnessReads;

    // What we last told each panel to do (DeviceName → percent). In absolute
    // mode two panels can legitimately sit at different percentages while
    // showing the same luminance — and a panel that hits its cap keeps
    // reporting 100 % — so "do the numbers match each other" is the wrong
    // question. "Does each panel still match what we asked it for" is the
    // right one; anything else means the user touched an OSD button.
    private readonly Dictionary<string, int> _lastAppliedPercent = new(StringComparer.OrdinalIgnoreCase);

    // DeviceName → does this panel actually honour brightness writes? Some
    // panels ACK the DDC/CI write but keep their own level (reads still work).
    // A write-ignored panel must never be treated as "the user moved it":
    // its permanent mismatch would drag the whole setup to its level. WMI
    // channels are always compliant; DDC/CI ones are probed once at startup.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _writeCompliant = new(StringComparer.OrdinalIgnoreCase);

    // One physical panel wired over two cables (HDMI + Type-C) appears as two
    // GDI devices. Both links must stay in _brightnessMonitors (writes go to
    // every link), but the UI shows ONE row per physical panel. CollapseDual-
    // LinkCardsAsync fills this map: hidden device -> visible device. Empty =
    // no merging. Keyed by GDI name so PeakNitsFor / peaks / status all agree.
    private readonly Dictionary<string, string> _dualLinkVisibleDevice =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Last known dual-link verdict, cached across rebuilds so refresh can show
    /// the collapsed state IMMEDIATELY instead of flashing all links for the
    /// ~2 s that the DDC probe takes. Each entry: panel model -> (member GDI
    /// names, chosen visible link). Cleared only when the topology changes.
    /// </summary>
    private sealed record DualLinkVerdict(string Model, HashSet<string> Names, string Primary);
    private readonly List<DualLinkVerdict> _dualLinkVerdicts = new();

    /// <summary>The representative (visible) GDI device for a possibly hidden link.</summary>
    private string VisibleDeviceOf(string device) =>
        _dualLinkVisibleDevice.TryGetValue(device, out var visible) ? visible : device;

    // ── Link watchdog state ────────────────────────────────────────────
    //
    // Snapshot of the active monitor topology (GDI name + panel model) as
    // of the previous 4-second tick; compared against the current one to
    // detect signal loss (a panel model's count went down) and duplicate
    // links (the same model suddenly reachable through two DISPLAYn). Alerts
    // are throttled per model so a flapping cable can't spam the desktop.
    private List<(string Device, string Model)>? _lastTopology;
    private readonly Dictionary<string, DateTime> _linkAlertCooldown = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LinkAlertCooldownWindow = TimeSpan.FromSeconds(90);

    [ObservableProperty]
    private bool _holdWhenWindowHidden = true;

    /// <summary>
    /// Keep the main window at the same physical (pixel) size when it crosses a
    /// monitor boundary with a different DPI scale. See
    /// <see cref="AppSettings.WindowSizeSyncEnabled"/> for the why.
    /// </summary>
    [ObservableProperty]
    private bool _isWindowScaleSyncEnabled = true;

    // Tape-measured viewable width per monitor model (see
    // AppSettings.MonitorWidthCm). Read-only for everyone except SaveSettings;
    // the window passes this to WindowScaleService so the cross-monitor size
    // sync can be ruler-accurate even when a panel's EDID lies about its size.
    private Dictionary<string, double> _monitorWidthCm = new(StringComparer.OrdinalIgnoreCase);

    // Diagonal inches entered per physical panel (model id → inches). Same key
    // strategy as MonitorWidthCm so both cables of a dual-link panel share one
    // value. Preferred source of real PPI in WindowScaleService: the user types
    // the number from the screen's box and no ruler / EDID trust is required.
    private Dictionary<string, double> _monitorDiagonalInches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Passed to WindowScaleService so cross-monitor size sync uses
    /// the user-entered diagonal instead of a possibly-faked EDID width.</summary>
    public IReadOnlyDictionary<string, double> MonitorDiagonalInchesOverride => _monitorDiagonalInches;

    /// <summary>One editable row per physical screen in the window-size-sync expander.</summary>
    public ObservableCollection<ScreenSizeEntryViewModel> ScreenSizeEntries { get; } = new();

    [ObservableProperty]
    private bool _isScreenSizeEditorExpanded;

    /// <summary>Chevron toggle for the screen-diagonal editor.</summary>
    [RelayCommand]
    private void ToggleScreenSizeEditor()
    {
        IsScreenSizeEditorExpanded = !IsScreenSizeEditorExpanded;
        if (IsScreenSizeEditorExpanded) RebuildScreenSizeEntries();
    }

    // ── User-defined display names / tags ────────────────────────────────
    //
    // Keyed by MonitorInfo.IdentityKey (per-unit device interface id), NOT by
    // \\.\DISPLAYx: display numbering changes with plug order, and the bare PnP
    // id is identical for two units of the same model.
    private Dictionary<string, string> _monitorNames = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _monitorTags = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, double> MonitorWidthCmOverride => _monitorWidthCm;

    // Physical size of the main window as of the last shutdown. Owned by the
    // window (it's the only thing that knows the real pixel size), parked here
    // because this is where every other persisted field lives. 0 = unknown.
    private int _windowPixelWidth;
    private int _windowPixelHeight;

    /// <summary>
    /// Pixel size recorded at the end of the previous session, or (0,0) when
    /// the app has never been closed cleanly. <see cref="Views.MainWindow"/>
    /// re-applies it at startup so a launch on a different-DPI monitor keeps the
    /// same on-screen size.
    /// </summary>
    public (int Width, int Height) RememberedWindowPixelSize =>
        (_windowPixelWidth, _windowPixelHeight);

    /// <summary>
    /// Called by the window when it closes, and after every cross-monitor
    /// resize. Persisted immediately so a crash doesn't lose the size.
    /// </summary>
    public void RememberWindowSize(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return;
        if (_windowPixelWidth == pixelWidth && _windowPixelHeight == pixelHeight) return;

        _windowPixelWidth = pixelWidth;
        _windowPixelHeight = pixelHeight;

        if (!_isLoading) SaveSettings();
    }

    [ObservableProperty]
    private bool _isBrightnessSyncEnabled;

    /// <summary>
    /// Software color sync: all monitors share one color temperature / eye-care
    /// setting, edited from any card on the screen-details page. Switching it on
    /// seeds the shared value from the primary monitor, exactly like
    /// <see cref="IsBrightnessSyncEnabled"/> does for luminance.
    /// </summary>
    [ObservableProperty]
    private bool _isColorSyncEnabled;

    /// <summary>
    /// Align panels on real luminance (nits) instead of the 0–100 % setting.
    /// A 400-nit secondary at 50 % is much dimmer than a 600-nit primary at
    /// 50 %, so percentage sync looks uneven on mixed hardware.
    /// </summary>
    [ObservableProperty]
    private bool _absoluteBrightnessSync;

    /// <summary>
    /// Collapse/expand state of the absolute-brightness details block
    /// (description + per-monitor peak-nits inputs). Kept out of settings
    /// on purpose: purely cosmetic, collapsed on every launch.
    /// </summary>
    [ObservableProperty]
    private bool _absoluteDetailsExpanded;

    /// <summary>Arrow button next to the absolute-brightness checkbox.</summary>
    [RelayCommand]
    private void ToggleAbsoluteDetails() => AbsoluteDetailsExpanded = !AbsoluteDetailsExpanded;

    /// <summary>Editable peak-nits row per controllable monitor.</summary>
    public ObservableCollection<BrightnessPeakEntry> BrightnessPeaks { get; } = new();

    [ObservableProperty]
    private int _brightnessValue = 50;

    [ObservableProperty]
    private string _brightnessStatus = string.Empty;

    [ObservableProperty]
    private bool _hasBrightnessControl;

    private bool _isLoading;
    // Volatile: read on the COM RPC thread in OnDeviceChangeDetected, written
    // on the UI thread in Dispose(). Without the barrier a late callback can
    // miss the write and queue dispatcher work during shutdown.
    private volatile bool _disposed;

    // Debounce window for coalescing bursts of device events. Plugging in a USB
    // DAC routinely fires OnDeviceAdded + OnDeviceStateChanged + OnDefaultDeviceChanged
    // back-to-back; without this we'd rebuild the dropdown three times in a row.
    private static readonly TimeSpan DeviceRefreshDebounce = TimeSpan.FromMilliseconds(300);

    // ── Primary-display output policy: never blank + follow headphones ──────
    // The primary card always shows a real playback device. DesiredAudioDeviceId
    // stays the user's "original output" (the fallback), while a newly appeared
    // endpoint (headphones just plugged) is followed TEMPORARILY and never
    // written back to the saved intent — so unplugging restores the base.
    private string? _primaryAutoDeviceId;
    private readonly HashSet<string> _knownRenderDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastSystemDefaultDeviceId;
    private bool _renderDeviceSnapshotReady;

    // A panel wired over two links (e.g. HDMI on the dGPU + Type-C on the iGPU)
    // exposes two render endpoints with the same EDID name. Windows reports both
    // as Active, yet only the link the panel is currently listening to carries
    // audio. The device IDs the user has actually saved in mappings/app pins are
    // the reliable signal of which twin is the live one; the other is a ghost
    // and gets hidden from the dropdowns.
    private readonly HashSet<string> _preferredAudioDeviceIds = new(StringComparer.OrdinalIgnoreCase);

    private const string AutoStartRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryName = "SimpleScreen";

    // Releases ≤ 2.1.0 registered under this name; cleaned up so an upgraded
    // install doesn't end up with TWO Run entries launching different exes.
    private const string LegacyAppRegistryName = "ScreenSound";

    private readonly Dictionary<uint, ImageSource?> _iconCache = new();

    public ObservableCollection<MonitorMappingViewModel> Monitors { get; } = new();
    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; } = new();
    public ObservableCollection<SessionDisplayViewModel> ActiveSessions { get; } = new();

    /// <summary>
    /// Per-app device pins shown in the "Pinned Apps" page and the source of
    /// truth for what the routing engine sees. Process name is the lookup key
    /// (case-insensitive). Source of additions is the right-click menu on an
    /// active session; removals come from the pin button on this page or the
    /// "Remove pin" entry on the session's context menu.
    /// </summary>
    public ObservableCollection<AppOverrideViewModel> AppOverrides { get; } = new();

    [ObservableProperty]
    private bool _isRoutingEnabled;

    [ObservableProperty]
    private int _monitorCount;

    [ObservableProperty]
    private int _deviceCount;

    [ObservableProperty]
    private bool _autoStartWithWindows;

    [ObservableProperty]
    private bool _minimizeOnClose;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private int _selectedThemeIndex; // 0 = System, 1 = Light, 2 = Dark

    /// <summary>
    /// Localised labels for the theme dropdown. Deliberately an
    /// <see cref="ObservableCollection{T}"/> (rather than a string[]) so a
    /// language switch can replace the three labels in place: swapping
    /// ItemsSource wholesale makes the ComboBox drop its selection and write
    /// -1 back into <see cref="SelectedThemeIndex"/>, silently resetting the
    /// user's theme.
    /// </summary>
    public ObservableCollection<string> ThemeOptions { get; } = new();

    [ObservableProperty]
    private int _selectedLanguageIndex; // 0 = English, 1 = 简体中文

    /// <summary>
    /// The language picker is intentionally NOT localised — a picker that
    /// renames its own entries is precisely what strands a user who picked a
    /// language they can't read.
    /// </summary>
    public string[] LanguageOptions { get; } = { "English", "简体中文", "日本語", "한국어" };

    /// <summary>ComboBox index → language code. Order matches <see cref="LanguageOptions"/>.</summary>
    private static string LanguageCodeForIndex(int index) => index switch
    {
        1 => LocalizationService.Chinese,
        2 => LocalizationService.Japanese,
        3 => LocalizationService.Korean,
        _ => LocalizationService.English
    };

    /// <summary>Language code → ComboBox index.</summary>
    private static int LanguageIndexForCode(string code) => code switch
    {
        LocalizationService.Chinese => 1,
        LocalizationService.Japanese => 2,
        LocalizationService.Korean => 3,
        _ => 0
    };

    [ObservableProperty]
    private int _currentPage; // 0 = Home, 1 = Pinned, 2 = Settings, 3 = About, 4 = Hotkeys

    // --- Hotkeys page bindings ---------------------------------------------
    //
    // The "jump window" hotkey: press it and the foreground window hops to the
    // other display (centred for normal windows, maximised windows fill the
    // target screen). Modifiers use RegisterHotKey's MOD_* bit flags so they
    // can round-trip through settings.json without a string table.

    [ObservableProperty]
    private bool _isJumpHotkeyEnabled = true;

    [ObservableProperty]
    private int _jumpHotkeyModifiers = 1; // MOD_ALT|MOD_CONTROL|MOD_SHIFT|MOD_WIN bits

    [ObservableProperty]
    private int _jumpHotkeyKey = 0x46; // virtual-key code, default 'F'

    /// <summary>
    /// RegisterHotKey failure shown on the hotkeys page ("combination already
    /// taken by another program"). Empty = no problem. Written by MainWindow
    /// (which owns the HWND registration), cleared on any successful re-register.
    /// </summary>
    [ObservableProperty]
    private string _hotkeyStatus = string.Empty;

    /// <summary>Rendered key badge, e.g. "Alt + F" or just "F".</summary>
    public string JumpHotkeyDisplay => FormatHotkey(JumpHotkeyModifiers, JumpHotkeyKey);

    /// <summary>Left key-slot label (modifier cell on the hotkeys page).</summary>
    public string JumpModifierDisplay => FormatModifiers(JumpHotkeyModifiers);

    /// <summary>Right key-slot label (main-key cell on the hotkeys page).</summary>
    public string JumpKeyDisplay => FormatKey(JumpHotkeyKey);

    public static string FormatModifiers(int modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & (int)Interop.NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & (int)Interop.NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & (int)Interop.NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & (int)Interop.NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        return parts.Count > 0 ? string.Join(" + ", parts) : "—";
    }

    public static string FormatKey(int vk)
        => vk > 0 ? System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk).ToString() : "—";

    public static string FormatHotkey(int modifiers, int vk)
    {
        var parts = new List<string>();
        if ((modifiers & (int)Interop.NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & (int)Interop.NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & (int)Interop.NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & (int)Interop.NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        if (vk > 0)
            parts.Add(FormatKey(vk));
        return parts.Count > 0 ? string.Join(" + ", parts) : "—";
    }

    partial void OnIsJumpHotkeyEnabledChanged(bool value)
    {
        if (!_isLoading) SaveSettings();
    }

    partial void OnJumpHotkeyModifiersChanged(int value)
    {
        OnPropertyChanged(nameof(JumpHotkeyDisplay));
        OnPropertyChanged(nameof(JumpModifierDisplay));
        if (!_isLoading) SaveSettings();
    }

    partial void OnJumpHotkeyKeyChanged(int value)
    {
        OnPropertyChanged(nameof(JumpHotkeyDisplay));
        OnPropertyChanged(nameof(JumpKeyDisplay));
        if (!_isLoading) SaveSettings();
    }

    // --- Tile / auto-arrange bindings --------------------------------------
    //
    // The U hotkey tiles the secondary display on demand; the auto-arrange
    // switch does the same thing without a key press whenever a new window
    // shows up or free space opens up (see AutoArrangeWatcher).

    /// <summary>Enables the U hotkey for on-demand tiling (independent of auto-arrange).</summary>
    [ObservableProperty]
    private bool _isArrangeHotkeyEnabled = true;

    partial void OnIsArrangeHotkeyEnabledChanged(bool value)
    {
        if (!_isLoading) SaveSettings();
    }
    [ObservableProperty]
    private bool _isAutoArrangeEnabled;

    [ObservableProperty]
    private int _arrangeHotkeyModifiers = 1; // MOD_* bits

    [ObservableProperty]
    private int _arrangeHotkeyKey = 0x55; // virtual-key code, default 'U'

    /// <summary>RegisterHotKey failure for the tile binding; empty = OK.</summary>
    [ObservableProperty]
    private string _arrangeHotkeyStatus = string.Empty;

    public string ArrangeHotkeyDisplay => FormatHotkey(ArrangeHotkeyModifiers, ArrangeHotkeyKey);

    public string ArrangeModifierDisplay => FormatModifiers(ArrangeHotkeyModifiers);

    public string ArrangeKeyDisplay => FormatKey(ArrangeHotkeyKey);

    partial void OnIsAutoArrangeEnabledChanged(bool value)
    {
        if (!_isLoading) SaveSettings();
    }

    partial void OnArrangeHotkeyModifiersChanged(int value)
    {
        OnPropertyChanged(nameof(ArrangeHotkeyDisplay));
        OnPropertyChanged(nameof(ArrangeModifierDisplay));
        if (!_isLoading) SaveSettings();
    }

    partial void OnArrangeHotkeyKeyChanged(int value)
    {
        OnPropertyChanged(nameof(ArrangeHotkeyDisplay));
        OnPropertyChanged(nameof(ArrangeKeyDisplay));
        if (!_isLoading) SaveSettings();
    }

    // --- About page bindings ---------------------------------------------
    //
    // AppVersionDisplay is read once at construction from the assembly's
    // informational version so the About page always shows the build that's
    // actually running — no more hardcoded "Version 1.0.0" drifting behind
    // the actual release tag.
    //
    // UpdateStatus is a single line of text rendered under the "Check for
    // updates" button. States cycle through:
    //   ""                    — idle, button hidden-label state
    //   "Checking…"           — request in flight
    //   "You're up to date"   — latest == current
    //   "vX.Y.Z available — click to open"   — newer release found
    //   "Couldn't reach GitHub: …"           — network/rate-limit failure
    //
    // LatestReleaseUrl is non-empty only when an update is available; the XAML
    // binds its visibility so the "Download" hyperlink appears only then.

    public string AppVersionDisplay =>
        LocalizationService.Format("About_Version", _updateService.CurrentVersion);

    /// <summary>
    /// Copyright footer rendered at the bottom of the About page. Takes the
    /// standard "Copyright (c) YYYY …" string emitted by MSBuild and swaps the
    /// "(c)" for a proper © glyph — same info, cleaner typography. We do the
    /// swap here rather than in the csproj because the Win32 file-properties
    /// dialog (and some installer pipelines) prefer the ASCII form.
    /// </summary>
    public string CopyrightDisplay =>
        _updateService.Copyright.Replace("Copyright (c)", "©", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    private string _latestReleaseUrl = string.Empty;

    [ObservableProperty]
    private bool _isCheckingForUpdate;

    // The status line is rendered from a resource key + arguments rather than a
    // pre-formatted string, so a language switch can re-render it verbatim.
    private string _updateStatusKey = string.Empty;
    private object?[] _updateStatusArgs = Array.Empty<object?>();

    private void SetUpdateStatus(string key, params object?[] args)
    {
        _updateStatusKey = key;
        _updateStatusArgs = args;
        UpdateStatus = LocalizationService.Format(key, args);
    }

    public MainWindowViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;

        _monitorService = new MonitorService();
        _audioDeviceService = new AudioDeviceService();
        _sessionService = new AudioSessionService();
        _routerService = new AudioRouterService();
        _settingsService = new SettingsService();
        _updateService = new UpdateService();
        _brightnessService = new BrightnessService();
        _routingEngine = new RoutingEngine(_monitorService, _sessionService, _routerService);

        _routingEngine.SessionsUpdated += OnSessionsUpdated;

        // Debounced timer for device hot-plug refreshes. Constructed bound to the
        // UI dispatcher so the Tick handler runs on the UI thread and can safely
        // mutate the AudioDevices ObservableCollection.
        _deviceRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = DeviceRefreshDebounce
        };
        _deviceRefreshTimer.Tick += OnDeviceRefreshTimerTick;

        // 1 s poll that keeps monitors brightness-locked while sync is enabled.
        _brightnessPollTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _brightnessPollTimer.Tick += OnBrightnessPollTick;

        // 250 ms debounce between slider drags and the (slow) DDC/CI fan-out.
        _brightnessApplyTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _brightnessApplyTimer.Tick += OnBrightnessApplyTimerTick;

        // 4 s link watchdog: notices monitors disappearing (signal loss) and
        // the same panel arriving on a second cable (duplicate). Enumeration
        // is a cheap EnumDisplayDevices walk; the first tick only takes the
        // baseline, so plugging/unplugging while the app starts never pops a
        // stale alert.
        _linkWatchTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _linkWatchTimer.Tick += OnLinkWatchTick;
        _linkWatchTimer.Start();

        _deviceNotifier = new AudioDeviceNotifier();
        _deviceNotifier.DevicesChanged += OnDeviceChangeDetected;
        _deviceNotifier.DefaultDeviceChanged += OnDeviceChangeDetected;
        _deviceNotifier.Start();

        // Strings consumed by the XAML update themselves through
        // {DynamicResource}; everything else is rebuilt from this hook.
        LocalizationService.LanguageChanged += OnLanguageChanged;
        RefreshThemeOptions();

        LoadDevicesAndMonitors();
        LoadSettings();
        _ = InitializeBrightnessAsync();
    }

    private void LoadDevicesAndMonitors()
    {
        AudioDevices.Clear();
        Monitors.Clear();

        AudioDevices.Add(new AudioDeviceInfo { Id = "", FriendlyName = LocalizationService.Get("Text_NoMapping") });

        var currentDevices = _audioDeviceService.GetOutputDevices();
        foreach (var device in currentDevices)
        {
            AudioDevices.Add(device);
        }

        // Baseline for hot-plug detection. Everything connected at startup is
        // "already known", so only a genuinely new device triggers auto-follow.
        // A full Refresh rebuild also goes through here — reseeding is correct
        // because the set present now is the new normal.
        _knownRenderDeviceIds.Clear();
        foreach (var device in currentDevices)
            _knownRenderDeviceIds.Add(device.Id);
        _lastSystemDefaultDeviceId = currentDevices.FirstOrDefault(d => d.IsDefault)?.Id;
        _renderDeviceSnapshotReady = true;

        _monitorService.InvalidateCache();
        foreach (var monitor in _monitorService.GetMonitors(forceRefresh: true))
        {
            var vm = new MonitorMappingViewModel(monitor, AudioDevices);
            vm.MappingChanged += OnMappingChanged;
            vm.VolumeChanged += OnVolumeChanged;
            vm.DevicePickedByUser += OnDevicePickedByUser;
            Monitors.Add(vm);
        }

        MonitorCount = Monitors.Count;
        DeviceCount = AudioDevices.Count - 1;

        // Names/tags are stored per identity key and applied onto the freshly
        // enumerated MonitorInfo instances (which start out blank).
        ApplyCustomMonitorNames();

        // Brand-new cards all start visible. Replay the last known dual-link
        // verdict synchronously so a refresh never flashes every cable as a
        // separate card before the (slow) async probe catches up.
        ApplyCachedDualLinkState();
    }

    /// <summary>
    /// Applies the cached <see cref="_dualLinkVerdicts"/> onto the CURRENT card
    /// collections with no I/O — safe to call repeatedly and from a rebuild.
    /// This is the instant path; <see cref="CollapseDualLinkCardsAsync"/> is the
    /// authoritative path that refreshes the verdict from live topology.
    /// </summary>
    private void ApplyCachedDualLinkState()
    {
        foreach (var vm in Monitors) { vm.IsHidden = false; vm.IsDualLink = false; }
        foreach (var card in MonitorDetails) { card.IsHidden = false; card.ResetDualLink(); }
        _dualLinkVisibleDevice.Clear();

        foreach (var verdict in _dualLinkVerdicts)
        {
            foreach (var name in verdict.Names)
            {
                var hidden = !string.Equals(name, verdict.Primary, StringComparison.OrdinalIgnoreCase);
                if (hidden) _dualLinkVisibleDevice[name] = verdict.Primary;
                foreach (var vm in Monitors.Where(v =>
                             string.Equals(v.Monitor.DeviceName, name, StringComparison.OrdinalIgnoreCase)))
                    vm.IsHidden = hidden;
                foreach (var card in MonitorDetails.Where(c =>
                             string.Equals(c.Monitor.DeviceName, name, StringComparison.OrdinalIgnoreCase)))
                    card.IsHidden = hidden;
            }

            var visible = MonitorDetails.FirstOrDefault(c =>
                string.Equals(c.Monitor.DeviceName, verdict.Primary, StringComparison.OrdinalIgnoreCase));
            visible?.SetDualLink();
            foreach (var vm in Monitors.Where(v =>
                         string.Equals(v.Monitor.DeviceName, verdict.Primary, StringComparison.OrdinalIgnoreCase)))
                vm.IsDualLink = true;
        }

        // The set of visible physical screens just settled — keep the diagonal
        // editor's rows in sync (one per visible panel, dual-link collapsed).
        RebuildScreenSizeEntries();
    }

    // ── Screen diagonal editor (window-size sync) ────────────────────────
    //
    // Rebuilt from the visible (non-hidden) monitor cards so it always mirrors
    // the physical screens the user actually sees: one primary + N secondary.
    // Dual-link cables of a single panel share a model id, so they collapse to
    // one row automatically. Values live in _monitorDiagonalInches keyed by
    // that model id and are persisted on every edit.

    /// <summary>
    /// Rebuilds the editable diagonal rows from the current visible monitors.
    /// Safe to call repeatedly; keeps entered values via the model-keyed dict.
    /// </summary>
    private void RebuildScreenSizeEntries()
    {
        var visible = Monitors.Where(m => m.IsCardVisible).ToList();
        if (visible.Count == 0) return;

        // Preserve any in-progress rows when the set of screens hasn't changed
        // (e.g. a refresh that only altered DPI), so a currently-focused text
        // box is not torn down mid-edit.
        var keys = string.Join("|", visible
            .Select(m => WindowScaleService.GetMonitorModel(m.Monitor.DeviceName) ?? m.Monitor.DeviceName)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        if (keys == _screenSizeEntryKey && ScreenSizeEntries.Count > 0) return;
        _screenSizeEntryKey = keys;

        ScreenSizeEntries.Clear();
        foreach (var m in visible.OrderByDescending(m => m.Monitor.IsPrimary))
        {
            var model = WindowScaleService.GetMonitorModel(m.Monitor.DeviceName) ?? m.Monitor.DeviceName;
            _monitorDiagonalInches.TryGetValue(model, out var inches);

            var label = m.Monitor.IsPrimary
                ? LocalizationService.Get("SS_PrimaryScreen")
                : string.Format(LocalizationService.Get("SS_SecondaryScreen"),
                    ScreenSizeEntries.Count); // 1-based among secondary rows

            ScreenSizeEntries.Add(new ScreenSizeEntryViewModel(
                model,
                label,
                $"{m.Monitor.Width}×{m.Monitor.Height}",
                m.Monitor.IsPrimary,
                inches,
                (key, value) => _monitorDiagonalInches[key] = value,
                SaveSettings));
        }
    }
    private string _screenSizeEntryKey = string.Empty;

    /// <summary>Re-localises row labels after a language switch without rebuilding
    /// the rows (which would drop a text box that currently has focus).</summary>
    private void RefreshScreenSizeEntryLabels()
    {
        var secondary = 0;
        foreach (var entry in ScreenSizeEntries)
        {
            entry.DisplayName = entry.IsPrimary
                ? LocalizationService.Get("SS_PrimaryScreen")
                : string.Format(LocalizationService.Get("SS_SecondaryScreen"), ++secondary);
        }
    }

    // ── User-defined display names / tags ────────────────────────────────

    /// <summary>
    /// Applies the saved names/tags onto every live <see cref="MonitorInfo"/>
    /// instance. Several copies exist (home mappings, brightness rows, details
    /// cards) because each subsystem refreshes its own monitor list — all are
    /// keyed by IdentityKey, so one pass keeps them consistent.
    /// </summary>
    private void ApplyCustomMonitorNames()
    {
        foreach (var vm in Monitors)
        {
            ApplyIdentity(vm.Monitor);
            vm.RefreshDisplayText();
        }

        foreach (var detail in MonitorDetails)
        {
            ApplyIdentity(detail.Monitor);
            detail.RefreshIdentity();
        }

        foreach (var bm in _brightnessMonitors)
            ApplyIdentity(bm.Monitor);

        // Peak rows carry a prebuilt label rather than the MonitorInfo itself.
        foreach (var peak in BrightnessPeaks)
        {
            var bm = _brightnessMonitors.FirstOrDefault(m =>
                m.Monitor.DeviceName.Equals(peak.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (bm != null) peak.DisplayName = PeakRowName(bm.Monitor);
        }
    }

    private void ApplyIdentity(MonitorInfo monitor)
    {
        monitor.CustomName = monitor.IdentityKey.Length > 0
                             && _monitorNames.TryGetValue(monitor.IdentityKey, out var name)
            ? name : "";
        monitor.Tag = monitor.IdentityKey.Length > 0
                      && _monitorTags.TryGetValue(monitor.IdentityKey, out var tag)
            ? tag : "";
    }

    private static string PeakRowName(MonitorInfo monitor) =>
        monitor.IsPrimary
            ? $"{monitor.BaseDisplayName} ({LocalizationService.Get("Home_Primary")})"
            : monitor.BaseDisplayName;

    /// <summary>
    /// Called from a details card when the user saves a name/tag. Empty values
    /// remove the override so the panel falls back to its hardware name.
    /// </summary>
    private void SaveMonitorIdentity(MonitorInfo monitor, string name, string tag)
    {
        var key = monitor.IdentityKey;
        if (string.IsNullOrEmpty(key)) return;

        name = (name ?? "").Trim();
        tag = (tag ?? "").Trim();
        if (name.Length == 0) _monitorNames.Remove(key); else _monitorNames[key] = name;
        if (tag.Length == 0) _monitorTags.Remove(key); else _monitorTags[key] = tag;

        ApplyCustomMonitorNames();
        SaveSettings();
    }

    /// <summary>
    /// Hot-plug entry point. Runs on a COM RPC thread. All it does is restart
    /// the debounce timer on the UI thread — the actual refresh happens once
    /// the burst of events settles.
    /// </summary>
    private void OnDeviceChangeDetected()
    {
        if (_disposed) return;

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            // Stop+Start resets the elapsed time, so rapid-fire events keep
            // pushing the refresh out until the stream quiets down.
            _deviceRefreshTimer.Stop();
            _deviceRefreshTimer.Start();
        });
    }

    private void OnDeviceRefreshTimerTick(object? sender, EventArgs e)
    {
        _deviceRefreshTimer.Stop();
        if (_disposed) return;
        RefreshAudioDevicesPreservingSelections();
    }

    /// <summary>
    // Generic endpoint labels are shared by unrelated physical devices (two
    // different USB headsets are both just "耳机"). Never collapse those —
    // only monitor-named endpoints (e.g. "QHD (NVIDIA High Definition Audio)")
    // can be twin render links of one panel.
    private static readonly HashSet<string> GenericEndpointNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "耳机", "扬声器", "麦克风", "线路输入", "耳机麦克风",
        "Headphones", "Speakers", "Microphone", "Headset", "Line In"
    };

    private static string EndpointGroupKey(string friendlyName)
    {
        var name = friendlyName ?? string.Empty;
        var i = name.IndexOf(" (", StringComparison.Ordinal);
        if (i > 0) name = name[..i];
        return name.Trim();
    }

    /// <summary>
    /// Rebuild the set of device IDs the user has committed to (monitor maps +
    /// per-app pins). Saved IDs mark the live link of a dual-link panel; unsaved
    /// twins are hidden so the dropdown doesn't offer a device that can't play.
    /// </summary>
    private void RefreshPreferredDeviceIds(AppSettings settings)
    {
        _preferredAudioDeviceIds.Clear();
        foreach (var mapping in settings.Mappings)
            if (!string.IsNullOrWhiteSpace(mapping.AudioDeviceId))
                _preferredAudioDeviceIds.Add(mapping.AudioDeviceId);
        foreach (var kvp in settings.AppOverrides)
            if (!string.IsNullOrWhiteSpace(kvp.Value))
                _preferredAudioDeviceIds.Add(kvp.Value);
    }

    /// <summary>
    /// Collapse twin endpoints of the same physical panel. Windows exposes one
    /// render endpoint per link (HDMI/DP on each GPU) and marks them all Active,
    /// but only one link is actually carrying the panel's audio. Once the user
    /// has chosen one of them, keep that one and drop the ghost. Panels the user
    /// hasn't picked yet keep all options so the choice remains available.
    /// </summary>
    private List<AudioDeviceInfo> FilterDuplicateDisplayEndpoints(IReadOnlyList<AudioDeviceInfo> devices)
    {
        var kept = new List<AudioDeviceInfo>(devices.Count);
        foreach (var group in devices.GroupBy(d => EndpointGroupKey(d.FriendlyName)))
        {
            var items = group.ToList();
            if (items.Count < 2 || GenericEndpointNames.Contains(group.Key))
            {
                kept.AddRange(items);
                continue;
            }

            var preferred = items.Where(d => _preferredAudioDeviceIds.Contains(d.Id)).ToList();
            kept.AddRange(preferred.Count > 0 ? preferred : items);
        }
        return kept;
    }

    /// <summary>
    /// Rebuild <see cref="AudioDevices"/> from the service with the ghost
    /// endpoints filtered out. Used when the saved mappings become known after
    /// the initial device list was already built.
    /// </summary>
    private void RebuildAudioDeviceList()
    {
        var devices = FilterDuplicateDisplayEndpoints(_audioDeviceService.GetOutputDevices());
        AudioDevices.Clear();
        AudioDevices.Add(new AudioDeviceInfo { Id = string.Empty, FriendlyName = LocalizationService.Get("Text_NoMapping") });
        foreach (var device in devices)
            AudioDevices.Add(device);
        DeviceCount = AudioDevices.Count - 1;
    }

    /// <summary>
    /// Rebuilds <see cref="AudioDevices"/> from the current endpoint list while
    /// preserving per-monitor selections wherever the same device ID is still
    /// present. Secondary panels behave as before (a vanished device leaves the
    /// card blank, intent preserved for re-plug). The <b>primary</b> display is
    /// special: its dropdown is never left blank, and a newly connected device
    /// (headphones) is followed temporarily until it is unplugged, at which
    /// point the user's original output is restored. Must run on the UI thread.
    /// </summary>
    private void RefreshAudioDevicesPreservingSelections()
    {
        // Snapshot intent (DesiredAudioDeviceId), not the live selection — an
        // unplug/replug round-trip must not erase the saved mapping.
        var desiredByMonitor = Monitors.ToDictionary(
            m => m.Monitor.DeviceName,
            m => m.DesiredAudioDeviceId);

        // Ghost twin links of a dual-link panel are hidden from the dropdown.
        var current = FilterDuplicateDisplayEndpoints(_audioDeviceService.GetOutputDevices());

        // A device that wasn't in the last snapshot is a real plug-in event.
        // Follow it on the primary display; the FIRST snapshot at app start is
        // only a baseline, so devices already connected then are not "new".
        var appearedId = DetectPluggedHeadphone(current);
        if (appearedId != null)
            _primaryAutoDeviceId = appearedId;

        if (appearedId == null)
        {
            // Some headsets (or 3.5mm jacks on an existing endpoint) don't add
            // a new endpoint — Windows just moves the system default to them.
            // Treat that flip as a plug-in too.
            var defaultNow = current.FirstOrDefault(d => d.IsDefault)?.Id;
            if (_lastSystemDefaultDeviceId != null && defaultNow != null
                && !string.Equals(defaultNow, _lastSystemDefaultDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                var newDefault = current.FirstOrDefault(d => d.Id == defaultNow);
                if (newDefault != null && LooksLikeHeadset(newDefault.FriendlyName))
                    _primaryAutoDeviceId = defaultNow;
            }
        }

        _knownRenderDeviceIds.Clear();
        foreach (var device in current)
            _knownRenderDeviceIds.Add(device.Id);
        _lastSystemDefaultDeviceId = current.FirstOrDefault(d => d.IsDefault)?.Id;
        _renderDeviceSnapshotReady = true;

        // _isLoading suppresses SaveSettings while we mutate selections
        // programmatically — a hot-plug must not overwrite the user's saved
        // mappings just because a device went away.
        _isLoading = true;
        try
        {
            foreach (var m in Monitors)
                m.SuspendBlankGuard = true;

            AudioDevices.Clear();
            AudioDevices.Add(new AudioDeviceInfo { Id = string.Empty, FriendlyName = LocalizationService.Get("Text_NoMapping") });
            foreach (var device in current)
                AudioDevices.Add(device);

            DeviceCount = AudioDevices.Count - 1;

            foreach (var monitorVm in Monitors)
            {
                if (!desiredByMonitor.TryGetValue(monitorVm.Monitor.DeviceName, out var desiredId))
                    continue;

                if (!monitorVm.IsPrimaryDisplay)
                {
                    // Secondary panels: preserve the old behaviour — match by
                    // Id, a missing device leaves the card blank but keeps intent.
                    monitorVm.SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == desiredId);
                    continue;
                }

                // Primary: always resolve to a real device. Set programmatically
                // so auto-following a headset never rewrites the saved base ID.
                var target = ResolvePrimaryTarget(desiredId, current);
                monitorVm.SetDeviceProgrammatically(target);
            }
        }
        finally
        {
            foreach (var m in Monitors)
                m.SuspendBlankGuard = false;
            _isLoading = false;
        }

        // Push to engine even if mappings didn't semantically change — a newly
        // plugged device might make a previously-dangling mapping valid again,
        // and a newly removed one needs the engine to stop trying to route to it.
        PushMappingsToEngine();

        // Re-resolve names on existing pins (their device IDs are stable, but
        // the friendly-name string for an unplugged device flips between
        // "(Device not connected)" and the real name on hot-plug).
        RefreshAppOverrideDeviceNames();

        // Re-sync sliders in case a re-plugged device has a different volume
        // than the one we last remembered.
        RefreshAssignedVolumes();
    }

    /// <summary>
    /// Picks the device a user most likely just plugged in. A single newcomer
    /// is unambiguous; when several arrive at once we only follow one if its
    /// name looks like a headset, otherwise we refuse to guess (avoids a driver
    /// restarting its virtual device and stealing the primary output).
    /// </summary>
    private string? DetectPluggedHeadphone(List<AudioDeviceInfo> current)
    {
        if (!_renderDeviceSnapshotReady) return null;

        // Software enhancers (FxSound) can restart their virtual endpoint and
        // show up as "new" — ignore them so they can't steal the primary output.
        var appeared = current
            .Where(d => !_knownRenderDeviceIds.Contains(d.Id) && !LooksLikeVirtualEnhancer(d.FriendlyName))
            .ToList();
        if (appeared.Count == 0) return null;
        if (appeared.Count == 1) return appeared[0].Id;

        return appeared.FirstOrDefault(d => LooksLikeHeadset(d.FriendlyName))?.Id;
    }

    private static bool LooksLikeVirtualEnhancer(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Contains("fxsound", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeHeadset(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var key in new[] { "headset", "headphone", "耳机", "耳麦" })
            if (name.Contains(key, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Primary display target, in priority order: the temporarily followed
    /// plug-in device (if still present), the user's persistent base mapping,
    /// the Windows default output, then any real device. Returns null only when
    /// the machine has no active render endpoint at all.
    /// </summary>
    private AudioDeviceInfo? ResolvePrimaryTarget(string baseId, List<AudioDeviceInfo> realDevices)
    {
        if (realDevices.Count == 0) return null;

        if (!string.IsNullOrEmpty(_primaryAutoDeviceId))
        {
            var auto = realDevices.FirstOrDefault(d => d.Id == _primaryAutoDeviceId);
            if (auto != null) return auto;
            // The auto-followed device was unplugged — fall back to the base.
            _primaryAutoDeviceId = null;
        }

        if (!string.IsNullOrEmpty(baseId))
        {
            var baseDevice = realDevices.FirstOrDefault(d => d.Id == baseId);
            if (baseDevice != null) return baseDevice;
        }

        return realDevices.FirstOrDefault(d => d.IsDefault) ?? realDevices[0];
    }

    private List<AudioDeviceInfo> RealDevicesInDropdown() =>
        AudioDevices.Where(d => !string.IsNullOrEmpty(d.Id)).ToList();

    private void LoadSettings()
    {
        _isLoading = true;
        try
        {
            var settings = _settingsService.Load();

            // The device list was built before settings were read. Now that the
            // saved mappings identify the live link of dual-link panels, rebuild
            // it with the ghost twins filtered out. The mapping loop below
            // re-seeds every card against this new list.
            RefreshPreferredDeviceIds(settings);
            RebuildAudioDeviceList();

            foreach (var mapping in settings.Mappings)
            {
                var monitorVm = Monitors.FirstOrDefault(m => m.Monitor.DeviceName == mapping.MonitorDeviceName);
                if (monitorVm != null)
                {
                    // Set DesiredAudioDeviceId unconditionally so a hot-plug
                    // later in the session can resolve a device that was missing
                    // at app start. SelectedAudioDevice only gets set when the
                    // device is actually present right now.
                    monitorVm.DesiredAudioDeviceId = mapping.AudioDeviceId;

                    if (monitorVm.IsPrimaryDisplay)
                    {
                        // Primary must never start blank, even if the saved base
                        // device is currently unplugged. Fall back to default /
                        // first available; base intent is preserved for re-plug.
                        monitorVm.SuspendBlankGuard = true;
                        try { monitorVm.SetDeviceProgrammatically(
                            ResolvePrimaryTarget(mapping.AudioDeviceId, RealDevicesInDropdown())); }
                        finally { monitorVm.SuspendBlankGuard = false; }
                    }
                    else
                    {
                        var device = AudioDevices.FirstOrDefault(d => d.Id == mapping.AudioDeviceId);
                        if (device != null)
                            monitorVm.SelectedAudioDevice = device;
                    }
                }
            }

            // Fresh install (no saved mapping yet): seed the primary card so it
            // is never blank on the very first launch either.
            var primaryCard = Monitors.FirstOrDefault(m => m.IsPrimaryDisplay);
            if (primaryCard != null && primaryCard.HasNoDevice)
            {
                primaryCard.SuspendBlankGuard = true;
                try { primaryCard.SetDeviceProgrammatically(
                    ResolvePrimaryTarget(string.Empty, RealDevicesInDropdown())); }
                finally { primaryCard.SuspendBlankGuard = false; }
            }

            AutoStartWithWindows = settings.AutoStartWithWindows;
            // Re-apply on every launch: migrates the pre-rename "ScreenSound"
            // Run entry to "SimpleScreen" and refreshes a stale exe path.
            if (AutoStartWithWindows) ApplyAutoStart(true);
            MinimizeOnClose = settings.MinimizeOnClose;
            StartMinimized = settings.StartMinimized;
            IsBrightnessSyncEnabled = settings.IsBrightnessSyncEnabled;
            IsColorSyncEnabled = settings.ColorSyncEnabled;
            AbsoluteBrightnessSync = settings.AbsoluteBrightnessSync;
            SelectedThemeIndex = settings.ThemeMode switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };

            // The language table is already applied by App.OnStartup (so the
            // very first frame is painted in the right language); mirroring the
            // choice into the combo box here keeps the UI and the table in sync.
            // Applying again is a no-op when they already match.
            var language = LocalizationService.Normalize(settings.Language);
            SelectedLanguageIndex = LanguageIndexForCode(language);
            LocalizationService.Apply(language);

            // Rebuild the per-app pin list from disk. Resolve device names against
            // the current device list — pins whose device is currently unplugged
            // get a placeholder name but keep their ID, so they reactivate cleanly
            // when the device returns.
            //
            // Collapse duplicate keys case-insensitively on the way in. System.Text.Json
            // deserializes into a Dictionary with the default (ordinal) comparer, so a
            // hand-edited settings.json containing both "chrome" and "Chrome" would load
            // without complaint — but PushOverridesToEngine() / SaveSettings() build an
            // OrdinalIgnoreCase dict downstream and would throw on the collision,
            // aborting startup. Normalize here; last key wins, consistent with a
            // re-save of what we end up with.
            AppOverrides.Clear();
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in settings.AppOverrides)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                    continue;
                normalized[kvp.Key.Trim()] = kvp.Value;
            }
            foreach (var (processName, deviceId) in normalized)
            {
                var deviceName = AudioDevices.FirstOrDefault(d => d.Id == deviceId)?.FriendlyName
                                 ?? LocalizationService.Get("Text_DeviceNotConnected");
                AppOverrides.Add(new AppOverrideViewModel
                {
                    ProcessName = processName,
                    AudioDeviceId = deviceId,
                    AudioDeviceName = deviceName
                });
            }

            RefreshAssignedVolumes();

            PushMappingsToEngine();
            PushOverridesToEngine();
            IsRoutingEnabled = settings.IsEnabled;
            HoldWhenWindowHidden = settings.HoldRoutingWhenWindowHidden;
            _routingEngine.SetHoldWhenWindowHidden(settings.HoldRoutingWhenWindowHidden);

            IsWindowScaleSyncEnabled = settings.WindowSizeSyncEnabled;
            _windowPixelWidth = settings.WindowPixelWidth;
            _windowPixelHeight = settings.WindowPixelHeight;
            _monitorWidthCm = new Dictionary<string, double>(settings.MonitorWidthCm, StringComparer.OrdinalIgnoreCase);
            _monitorDiagonalInches = new Dictionary<string, double>(settings.MonitorDiagonalInches, StringComparer.OrdinalIgnoreCase);
            // LoadDevicesAndMonitors ran before LoadSettings, so the first
            // RebuildScreenSizeEntries saw an empty inches dict. Force a fresh
            // build now that saved values are available (same screen set, so
            // the normal key guard would otherwise skip it and leave 0″ rows).
            _screenSizeEntryKey = string.Empty;
            RebuildScreenSizeEntries();

            _monitorNames = new Dictionary<string, string>(settings.MonitorCustomNames, StringComparer.OrdinalIgnoreCase);
            _monitorTags = new Dictionary<string, string>(settings.MonitorTags, StringComparer.OrdinalIgnoreCase);
            ApplyCustomMonitorNames();

            IsJumpHotkeyEnabled = settings.JumpHotkeyEnabled;
            JumpHotkeyModifiers = settings.JumpHotkeyModifiers;
            JumpHotkeyKey = settings.JumpHotkeyKey;
            IsArrangeHotkeyEnabled = settings.ArrangeHotkeyEnabled;
            IsAutoArrangeEnabled = settings.AutoArrangeEnabled;
            ArrangeHotkeyModifiers = settings.ArrangeHotkeyModifiers;
            ArrangeHotkeyKey = settings.ArrangeHotkeyKey;
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>
    /// A real user pick on the primary display becomes the new base output and
    /// ends any temporary headphone auto-follow, so the manual choice sticks.
    /// </summary>
    private void OnDevicePickedByUser(MonitorMappingViewModel card)
    {
        if (!card.IsPrimaryDisplay) return;
        _primaryAutoDeviceId = null;
    }

    private void OnMappingChanged()
    {
        // During programmatic refreshes (LoadSettings, hot-plug refresh, Refresh
        // command) selections are assigned in a loop. Running the full push-mappings
        // + volume-read fanout for each one would do O(N²) NAudio COM calls on the
        // UI thread and fire N half-built routing pushes. Each programmatic caller
        // is responsible for invoking PushMappingsToEngine + RefreshAssignedVolumes
        // itself, once, after its loop finishes.
        if (_isLoading) return;

        PushMappingsToEngine();
        SaveSettings();

        // A manual pick of one twin of a dual-link panel marks it as the live
        // link; re-read the just-saved settings so the ghost stays hidden on
        // the next refresh.
        RefreshPreferredDeviceIds(_settingsService.Load());

        RefreshAssignedVolumes();
    }

    /// <summary>
    /// Reads the current master volume from each monitor's assigned audio device
    /// and syncs it to the monitor's <c>Volume</c> slider. Runs on the UI thread;
    /// each call makes one NAudio COM round-trip per assigned device, so callers
    /// should run it once after a batch of selection changes rather than per-change.
    /// </summary>
    private void RefreshAssignedVolumes()
    {
        foreach (var m in Monitors)
        {
            if (m.SelectedAudioDevice != null && !string.IsNullOrEmpty(m.SelectedAudioDevice.Id))
            {
                var vol = _audioDeviceService.GetDeviceVolume(m.SelectedAudioDevice.Id);
                m.Volume = (int)Math.Round(vol * 100);
            }
        }
    }

    private void OnVolumeChanged(MonitorMappingViewModel vm)
    {
        if (vm.SelectedAudioDevice == null || string.IsNullOrEmpty(vm.SelectedAudioDevice.Id))
            return;

        _audioDeviceService.SetDeviceVolume(vm.SelectedAudioDevice.Id, vm.Volume / 100f);
    }

    // ── Brightness sync ──────────────────────────────────────────────────

    /// <summary>
    /// Probe every monitor for a controllable brightness path (DDC/CI for
    /// external panels, WMI for the laptop's integrated display). Runs off the
    /// UI thread — DDC/CI probing takes 100–300 ms per monitor — then reports
    /// capability back and starts syncing if the feature was enabled in
    /// settings at startup.
    /// </summary>
    // Serialises full brightness re-initialisation. Startup and the Refresh
    // command both call into it, and a second pass while the first is still
    // waiting on slow DDC replies would rebuild the details cards underneath
    // the first one (and probe every panel twice). Cheap to hold: the whole
    // routine is a few seconds at worst.
    private readonly SemaphoreSlim _brightnessInitGate = new(1, 1);

    private async Task InitializeBrightnessAsync()
    {
        await _brightnessInitGate.WaitAsync();
        try
        {
            var monitors = _monitorService.GetMonitors(forceRefresh: true);
            var list = await Task.Run(() => _brightnessService.RefreshMonitors(monitors));

            _brightnessMonitors = list;
            HasBrightnessControl = list.Count > 0;
            _brightnessReady = true;
            _lastAppliedPercent.Clear();
            // Build details first: it collapses HDMI+Type-C links of one panel
            // and fills _dualLinkVisibleDevice. RebuildBrightnessPeaks relies on
            // that map to show ONE peak row per physical panel (not per cable).
            await BuildMonitorDetailsAsync(monitors);
            RebuildBrightnessPeaks();

            // Probe each panel for real write support in the background — the
            // sync loop must not stall on it (a probe takes up to ~1.5 s per
            // panel). Until a verdict arrives, panels are assumed compliant;
            // see _writeCompliant for why the assumption matters.
            _ = Task.Run(async () =>
            {
                foreach (var bm in list)
                {
                    var ok = bm.Transport == "WMI" ||
                             await _brightnessService.VerifyWriteSupportAsync(bm);
                    _writeCompliant[bm.Monitor.DeviceName] = ok;
                    LogBrightness($"PROBE {bm.Monitor.DeviceName} ({bm.Transport}) write-supported={ok}");
                }
            });

            if (IsBrightnessSyncEnabled && list.Count > 0)
            {
                await SyncFromSourceAsync(null);
                _brightnessPollTimer.Start();
            }

            UpdateBrightnessStatus();
        }
        catch (Exception ex)
        {
            LogBrightness($"INIT FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            System.Diagnostics.Debug.WriteLine($"[Brightness] init failed: {ex.Message}");
        }
        finally
        {
            _brightnessInitGate.Release();
        }
    }

    /// <summary>
    /// Per-tick poll logging is far too chatty for normal use. Set the
    /// SIMPLESCREEN_VERBOSE_LOG environment variable to "1" to turn it on —
    /// kept as a runtime switch (not a const) so a released build can be
    /// diagnosed without recompiling.
    /// </summary>
    private static readonly bool VerboseBrightnessLog =
        Environment.GetEnvironmentVariable("SIMPLESCREEN_VERBOSE_LOG") == "1";

    // brightness.log is a debugging aid, not an audit trail — cap it so months
    // of daily use can't grow it without bound. When it passes ~1 MB we keep
    // only the newest ~256 KB (where the actionable entries live anyway).
    private const long BrightnessLogMaxBytes = 1_000_000;
    private const long BrightnessLogKeepBytes = 256_000;

    private static void LogBrightness(string message)
    {
        try
        {
            var dir = SettingsService.SettingsDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "brightness.log");
            TrimBrightnessLogIfNeeded(path);
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n");
        }
        catch { /* logging must never break syncing */ }
    }

    private static void TrimBrightnessLogIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < BrightnessLogMaxBytes) return;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var keep = (int)Math.Min(BrightnessLogKeepBytes, fs.Length);
        var tail = new byte[keep];
        fs.Seek(-keep, SeekOrigin.End);
        var read = 0;
        while (read < keep)
        {
            var n = fs.Read(tail, read, keep - read);
            if (n <= 0) break;
            read += n;
        }
        // Drop a partial first line so the trimmed log starts at a record boundary.
        var start = Array.IndexOf(tail, (byte)'\n') + 1;
        if (start <= 0) start = 0;

        fs.SetLength(0);
        fs.Write(tail, start, read - start);
    }

    /// <summary>
    /// Push <paramref name="sliderValue"/> to every monitor. Null means "adopt
    /// the primary monitor's current value as the source of truth".
    /// </summary>
    private async Task SyncFromSourceAsync(int? sliderValue)
    {
        if (_brightnessMonitors.Count == 0) return;

        if (sliderValue == null)
        {
            var source = ReferenceMonitor;
            var current = await _brightnessService.GetBrightnessAsync(source);
            if (current == null) return;

            if (AbsoluteBrightnessSync)
            {
                // Seed from the primary's real luminance rather than its
                // percentage, so enabling the mode never dims the other panels.
                var nits = NitsOf(source, current.Value);
                await ApplyPercentsAsync(PercentagesForNits(nits), SliderPercentForNits(nits));
                return;
            }

            sliderValue = current.Value;
        }

        await ApplyToAllAsync(sliderValue.Value, "seed");
    }

    /// <summary>Primary panel if there is one, otherwise the first controllable one.</summary>
    private BrightnessService.BrightnessMonitor ReferenceMonitor =>
        _brightnessMonitors.FirstOrDefault(b => b.Monitor.IsPrimary) ?? _brightnessMonitors[0];

    private int PeakNitsFor(BrightnessService.BrightnessMonitor bm)
    {
        var device = VisibleDeviceOf(bm.Monitor.DeviceName);
        var entry = BrightnessPeaks.FirstOrDefault(p =>
            string.Equals(p.DeviceName, device, StringComparison.OrdinalIgnoreCase));
        return entry?.PeakNits ?? BrightnessPeakEntry.DefaultPeakNits;
    }

    /// <summary>
    /// Percent setting that makes <paramref name="bm"/> emit roughly
    /// <paramref name="nits"/>. Clamped to the panel's 0–100 range, which is why
    /// two panels can disagree on percentage yet match on luminance.
    /// </summary>
    private int PercentForNits(BrightnessService.BrightnessMonitor bm, double nits)
    {
        var peak = Math.Max(1, PeakNitsFor(bm));
        return Math.Clamp((int)Math.Round(nits * 100.0 / peak), 0, 100);
    }

    private Dictionary<string, int> PercentagesForNits(double nits)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var bm in _brightnessMonitors)
            result[bm.Monitor.DeviceName] = PercentForNits(bm, nits);
        return result;
    }

    /// <summary>
    /// Highest peak among the panels we can actually drive. The slider is
    /// scaled against this so 100 % always means "as bright as this setup can
    /// go" — otherwise putting the dim panel first on the desk would cap the
    /// whole sync at that panel's limit.
    /// </summary>
    private int MaxPeakNits
    {
        get
        {
            if (_brightnessMonitors.Count == 0) return BrightnessPeakEntry.DefaultPeakNits;
            return _brightnessMonitors.Max(PeakNitsFor);
        }
    }

    /// <summary>Slider read-out: absolute nits when in nits mode, else a plain percentage.</summary>
    public string SliderValueText => AbsoluteBrightnessSync
        ? LocalizationService.Format("Brightness_SliderNits",
            (int)Math.Round(MaxPeakNits * BrightnessValue / 100.0))
        : $"{BrightnessValue}%";

    /// <summary>
    /// Translate the slider position into a per-panel percentage map. In
    /// absolute mode 100 % is the brightest panel's peak; that luminance is
    /// converted for every panel's own peak and clamped, so a 400-nit panel
    /// stops at 400 nits while a 500-nit one keeps climbing.
    /// </summary>
    private Dictionary<string, int> BuildPercents(int sliderValue)
    {
        if (!AbsoluteBrightnessSync || _brightnessMonitors.Count == 0)
        {
            return _brightnessMonitors.ToDictionary(
                bm => bm.Monitor.DeviceName, _ => sliderValue, StringComparer.OrdinalIgnoreCase);
        }

        var targetNits = MaxPeakNits * (sliderValue / 100.0);
        return PercentagesForNits(targetNits);
    }

    /// <summary>Slider position whose absolute luminance is <paramref name="nits"/>.</summary>
    private int SliderPercentForNits(double nits) =>
        Math.Clamp((int)Math.Round(nits * 100.0 / Math.Max(1, MaxPeakNits)), 0, 100);

    private double NitsOf(BrightnessService.BrightnessMonitor bm, int percent) =>
        PeakNitsFor(bm) * (percent / 100.0);

    private Task ApplyToAllAsync(int sliderValue, string tag = "slider") =>
        ApplyPercentsAsync(BuildPercents(sliderValue), sliderValue, tag);

    private async Task ApplyPercentsAsync(Dictionary<string, int> percents, int sliderValue, string tag = "?")
    {
        if (_applyingBrightness) return;
        LogBrightness($"APPLY[{tag}] slider={sliderValue} abs={AbsoluteBrightnessSync} -> " +
                      string.Join(",", percents.Select(kv => $"{kv.Key}={kv.Value}")));
        _applyingBrightness = true;
        try
        {
            foreach (var bm in _brightnessMonitors)
            {
                if (percents.TryGetValue(bm.Monitor.DeviceName, out var percent))
                    await _brightnessService.SetBrightnessAsync(bm, percent);
            }

            _lastAppliedPercent.Clear();
            foreach (var kv in percents)
                _lastAppliedPercent[kv.Key] = kv.Value;

            _lastSyncedBrightness = sliderValue;
            BrightnessValue = sliderValue;

            // Refresh the readout from what we just applied, so it tracks the
            // slider immediately instead of waiting for the next poll. At this
            // instant the commanded values are the truth — the hardware
            // readback is still the pre-apply level (and frozen outright on a
            // write-unreliable panel), so feeding the raw sample back here
            // would briefly show the old number.
            var appliedReads = new List<(BrightnessService.BrightnessMonitor Bm, int Value)>();
            foreach (var bm in _brightnessMonitors)
                if (percents.TryGetValue(bm.Monitor.DeviceName, out var p))
                    appliedReads.Add((bm, p));
            UpdateBrightnessStatus(appliedReads);
        }
        finally
        {
            // Give panels time to reflect the new value before the next poll;
            // reading DDC/CI back too early yields the old level and would
            // trigger a bogus "user changed it" event.
            _brightnessSettleUntil = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            _applyingBrightness = false;
        }
    }

    private async void OnBrightnessPollTick(object? sender, EventArgs e)
    {
        if (!IsBrightnessSyncEnabled || !_brightnessReady) return;
        if (_applyingBrightness || DateTime.UtcNow < _brightnessSettleUntil) return;
        if (_brightnessMonitors.Count < 2) return;

        _brightnessPollTimer.Stop();
        try
        {
            var reads = new List<(BrightnessService.BrightnessMonitor Bm, int Value)>();
            foreach (var bm in _brightnessMonitors)
            {
                var v = await _brightnessService.GetBrightnessAsync(bm);
                if (v.HasValue) reads.Add((bm, v.Value));
            }

            if (VerboseBrightnessLog)
                LogBrightness($"POLL reads=[{string.Join(",", reads.Select(r => $"{r.Bm.Monitor.DeviceName}={r.Value}"))}] " +
                              $"expected=[{string.Join(",", _lastAppliedPercent.Select(kv => $"{kv.Key}={kv.Value}"))}]");

            // Panels that ignore writes never participate in the decisions
            // below: their level is frozen at whatever the firmware keeps, and
            // reading it as "drift" would drag every other panel to it.
            // A panel with no verdict yet is also excluded — the capability
            // probe runs in the background and can take several seconds, while
            // this poll ticks every second. Treating an unprobed panel as
            // compliant lets its stale readback fire a bogus DRIFT on startup
            // (e.g. the HIV0160 reporting 90 % while we just applied 0 %).
            var active = reads.Where(r =>
                _writeCompliant.TryGetValue(r.Bm.Monitor.DeviceName, out var ok) && ok).ToList();

            if (active.Count == 0)
            {
                // Nothing here can actually be driven — keep the status line
                // honest and leave everyone's level alone.
                UpdateBrightnessStatus(reads);
                return;
            }

            if (reads.Count < 2) return;

            int min = active.Min(r => r.Value);
            int max = active.Max(r => r.Value);

            bool matchesExpectation = _lastAppliedPercent.Count > 0 && active.All(r =>
                _lastAppliedPercent.TryGetValue(r.Bm.Monitor.DeviceName, out var expected) &&
                Math.Abs(r.Value - expected) <= 1);

            // Everything still sits where we put it — including the absolute
            // mode case where panels legitimately hold different percentages.
            if (matchesExpectation)
            {
                if (!AbsoluteBrightnessSync)
                {
                    // Percentage mode: panels share one number, so the average
                    // is a faithful slider position.
                    _lastSyncedBrightness = (min + max) / 2;
                    BrightnessValue = _lastSyncedBrightness;
                }
                // Absolute mode: NEVER derive the slider from the panel
                // percentages. They differ by design (400-nit panel at 100 %
                // next to a 500-nit one at 80 %), so averaging them would move
                // the target, trigger another apply, and creep the whole setup
                // upwards on every tick.
                UpdateBrightnessStatus(reads);
                return;
            }

            // No history yet (first tick after enabling sync / a monitor
            // refresh): start from the reference panel rather than guessing.
            if (_lastAppliedPercent.Count == 0 && max - min <= 1)
            {
                _lastSyncedBrightness = (min + max) / 2;
                if (AbsoluteBrightnessSync)
                {
                    var seed = active.FirstOrDefault(r => r.Bm.Monitor.IsPrimary).Bm != null
                        ? active.First(r => r.Bm.Monitor.IsPrimary)
                        : active[0];
                    BrightnessValue = SliderPercentForNits(NitsOf(seed.Bm, seed.Value));
                }
                else
                {
                    BrightnessValue = _lastSyncedBrightness;
                }
                UpdateBrightnessStatus(reads);
                return;
            }

            // One or more panels drifted → the user pressed an OSD button.
            // Adopt that panel's level as the new source of truth.
            BrightnessService.BrightnessMonitor? source = null;
            if (_lastAppliedPercent.Count > 0)
            {
                var changed = active.Where(r =>
                    !_lastAppliedPercent.TryGetValue(r.Bm.Monitor.DeviceName, out var expected) ||
                    Math.Abs(r.Value - expected) > 1).ToList();
                if (changed.Count > 0)
                {
                    // Several panels changed at once (e.g. all reset after the
                    // monitor slept): trust the primary one if it is among them.
                    source = (changed.FirstOrDefault(r => r.Bm.Monitor.IsPrimary)).Bm
                             ?? changed[0].Bm;
                }
            }
            source ??= active.FirstOrDefault(r => r.Bm.Monitor.IsPrimary).Bm ?? active[0].Bm;
            LogBrightness($"DRIFT source={source.Monitor.DeviceName}");

            if (AbsoluteBrightnessSync)
            {
                // Re-anchor on the touched panel's luminance, then re-derive
                // everyone else's percentage from it.
                var read = reads.First(r => ReferenceEquals(r.Bm, source)).Value;
                var nits = NitsOf(source, read);
                await ApplyPercentsAsync(PercentagesForNits(nits), SliderPercentForNits(nits));
            }
            else
            {
                var read = reads.First(r => ReferenceEquals(r.Bm, source)).Value;
                await ApplyToAllAsync(read, "drift");
            }

            UpdateBrightnessStatus();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Brightness] poll failed: {ex.Message}");
        }
        finally
        {
            if (IsBrightnessSyncEnabled)
                _brightnessPollTimer.Start();
        }
    }

    private void OnBrightnessApplyTimerTick(object? sender, EventArgs e)
    {
        _brightnessApplyTimer.Stop();
        if (!IsBrightnessSyncEnabled || !_brightnessReady) return;
        _ = ApplyToAllAsync(BrightnessValue, "timer");
    }

    private void UpdateBrightnessStatus(List<(BrightnessService.BrightnessMonitor Bm, int Value)>? reads = null)
    {
        // Remembered so a language switch can re-render the same status line in
        // the new language instead of clearing it.
        _lastBrightnessReads = reads;

        if (_brightnessMonitors.Count == 0)
        {
            BrightnessStatus = LocalizationService.Get("Brightness_NoMonitors");
            return;
        }

        // A dual-link panel is one physical screen shown as one row, so the
        // summary/status must count and list it once (not once per cable).
        var physicalBrightnessDevices = _brightnessMonitors
            .Where(b => string.Equals(VisibleDeviceOf(b.Monitor.DeviceName),
                                      b.Monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (reads == null || reads.Count == 0)
        {
            BrightnessStatus = LocalizationService.Format(
                "Brightness_MonitorSummary",
                physicalBrightnessDevices.Count,
                string.Join(", ", physicalBrightnessDevices.Select(b => b.Transport).Distinct()));
        }
        else
        {
            BrightnessStatus = string.Join("  ·  ", reads
                .Where(r => string.Equals(VisibleDeviceOf(r.Bm.Monitor.DeviceName),
                                          r.Bm.Monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
                .Select(r =>
                {
                    // Some panels (e.g. the HIV0160 DDC/CI link) accept writes
                    // but report a frozen level on readback. The probe already
                    // flagged those as write-unsupported for exactly that
                    // reason, so their readback is not a trustworthy picture
                    // of what's on screen — show the value we actually told
                    // the panel to use instead. Otherwise the readout claims
                    // "90% · 360 nits" while the panel is sitting dark at 0 %,
                    // which looks like the sync broke when it didn't.
                    var device = r.Bm.Monitor.DeviceName;
                    var percent = (!_writeCompliant.TryGetValue(device, out var compliant) || !compliant)
                                  && _lastAppliedPercent.TryGetValue(device, out var applied)
                        ? applied
                        : r.Value;

                    return AbsoluteBrightnessSync
                        ? LocalizationService.Format("Brightness_StatusItemNits",
                            r.Bm.Monitor.FriendlyName, percent,
                            (int)Math.Round(NitsOf(r.Bm, percent)))
                        : $"{r.Bm.Monitor.FriendlyName}: {percent}%";
                }));
        }
    }

    /// <summary>
    /// Rebuild the peak-nits editor rows from the controllable monitor list,
    /// seeding each row from the saved dictionary.
    /// </summary>
    private void RebuildBrightnessPeaks(Dictionary<string, int>? savedPeaks = null)
    {
        savedPeaks ??= _settingsService.Load().MonitorPeakNits;

        BrightnessPeaks.Clear();
        foreach (var bm in _brightnessMonitors)
        {
            // A dual-link panel is two GDI devices (HDMI + Type-C) but one
            // physical screen: show a single editable peak row under the
            // visible link's name. Writes still target both links (they live
            // in _brightnessMonitors), so the hidden one inherits this peak.
            var device = VisibleDeviceOf(bm.Monitor.DeviceName);
            if (!string.Equals(device, bm.Monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (BrightnessPeaks.Any(p =>
                    string.Equals(p.DeviceName, device, StringComparison.OrdinalIgnoreCase)))
                continue;

            var entry = new BrightnessPeakEntry
            {
                DeviceName = device,
                DisplayName = PeakRowName(bm.Monitor)
            };

            if (savedPeaks.TryGetValue(device, out var nits) && nits > 0)
                entry.PeakNits = nits;

            // Assigned after the seed value so loading doesn't write the file.
            // Editing a peak value re-derives every percentage (debounced: the
            // box updates per keystroke and DDC/CI writes are slow).
            entry.Changed = () =>
            {
                SaveSettings();
                // A new peak changes what 100 % of the slider means.
                OnPropertyChanged(nameof(SliderValueText));
                if (!IsBrightnessSyncEnabled || !AbsoluteBrightnessSync || !_brightnessReady) return;
                _brightnessApplyTimer.Stop();
                _brightnessApplyTimer.Start();
            };
            BrightnessPeaks.Add(entry);
        }

        // MaxPeakNits is derived from this list, so the slider caption changes too.
        OnPropertyChanged(nameof(SliderValueText));
    }

    /// <summary>
    /// Peak-nit values edited on the screen-details page. Merged over the
    /// home-page rows on save so the two editors stay consistent (the rows
    /// only exist for brightness-controllable monitors; details exist for all).
    /// </summary>
    private readonly Dictionary<string, int> _detailPeakNits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Validated color-preset values per monitor (see AppSettings.MonitorValidPresets).</summary>
    private readonly Dictionary<string, List<int>> _validPresets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Software color temperature / low-blue-light per monitor (gamma ramp).</summary>
    private readonly Dictionary<string, int> _colorKelvin = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _eyeCare = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, int> BuildMergedPeakNits()
    {
        var dict = BrightnessPeaks
            .GroupBy(p => p.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().PeakNits, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _detailPeakNits) dict[kv.Key] = kv.Value;
        return dict;
    }

    /// <summary>
    /// Rebuild the screen-details cards: refresh DDC handles, collect static
    /// facts off-thread, then let each card load its own DDC state lazily.
    /// </summary>
    private async Task BuildMonitorDetailsAsync(List<MonitorInfo> monitors)
    {
        LogBrightness("DETAILS build start");
        await Task.Run(() => _vcpService.Refresh(monitors));
        LogBrightness("DETAILS vcp refreshed");

        var audioNames = _audioDeviceService.GetOutputDevices()
            .Select(d => d.FriendlyName).ToList();
        LogBrightness($"DETAILS audio devices={audioNames.Count}");

        var saved = _settingsService.Load();
        _detailPeakNits.Clear();
        foreach (var kv in saved.MonitorPeakNits) _detailPeakNits[kv.Key] = kv.Value;

        foreach (var kv in saved.MonitorValidPresets) _validPresets[kv.Key] = kv.Value;
        foreach (var kv in saved.MonitorColorKelvin) _colorKelvin[kv.Key] = kv.Value;
        foreach (var kv in saved.MonitorEyeCare) _eyeCare[kv.Key] = kv.Value;

        var facts = await Task.Run(() => MonitorDetailsService.GetAllDetails(monitors, audioNames));

        var host = new MonitorDetailHost
        {
            Vcp = _vcpService,
            SaveSettings = SaveSettings,
            LoadPeakNits = name => _detailPeakNits.TryGetValue(name, out var v) && v > 0 ? v : null,
            StorePeakNits = (name, v) => _detailPeakNits[name] = v,
            LoadValidPresets = name => _validPresets.TryGetValue(name, out var v) && v.Count > 0 ? v : null,
            StoreValidPresets = (name, v) => _validPresets[name] = v,
            LoadColor = name => (_colorKelvin.TryGetValue(name, out var k) ? k : ColorGainService.NeutralKelvin,
                                 _eyeCare.TryGetValue(name, out var e) ? e : 0),
            StoreColor = (name, k, e) => { _colorKelvin[name] = k; _eyeCare[name] = e; },
            BroadcastColor = BroadcastColor,
            SaveIdentity = SaveMonitorIdentity
        };

        MonitorDetails.Clear();
        var cardNumber = 0;
        foreach (var m in monitors)
        {
            cardNumber++;
            try
            {
                var vm = new MonitorDetailItemViewModel(m, facts[m], host, cardNumber);
                MonitorDetails.Add(vm);
                _ = vm.LoadDdcStateAsync();
            }
            catch (Exception ex)
            {
                LogBrightness($"DETAILS card {cardNumber} FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
        LogBrightness($"DETAILS built cards={MonitorDetails.Count}");

        // Newly built detail cards all start visible. Replay the cached verdict
        // now so the details page also stays merged while the live probe runs.
        ApplyCachedDualLinkState();

        // Freshly built cards hold monitor instances that don't know the
        // user's names/tags yet.
        ApplyCustomMonitorNames();

        // Cards have applied their own saved tint by now, which can differ per
        // monitor if sync was switched on in a previous session. Re-unify on the
        // primary monitor's values so a restart doesn't undo the sync.
        if (IsColorSyncEnabled) ApplyColorSyncFromPrimary();

        await CollapseDualLinkCardsAsync(monitors);

        // Link diagnostics (connector type / bandwidth / duplicate links).
        RefreshLinkDiagnostics();
    }

    /// <summary>
    /// A single portable panel wired over two cables (HDMI + USB-C DP Alt)
    /// appears to Windows as two independent DISPLAYn devices. Both must stay
    /// in the collections — window placement, gamma ramps and audio routing
    /// address them by GDI name — but the UI shows ONE card for the panel.
    ///
    /// Evidence of a shared panel: same EDID model, exactly two instances,
    /// and two DIFFERENT connector technologies. Two identical monitors on the
    /// same kind of cable are deliberately not merged.
    ///
    /// The visible card is the link whose DDC/CI channel answers right now —
    /// that is the input the panel has selected — so its hardware controls
    /// stay usable. The hidden card still receives every broadcast (color
    /// sync, renaming, link diagnostics), keeping both links in step.
    /// </summary>
    private async Task CollapseDualLinkCardsAsync(List<MonitorInfo> monitors)
    {
        // Do NOT touch card visibility here at the start. A previous version
        // reset every card to visible, then spent ~2 s probing DDC before
        // hiding the twin — that produced the visible "3 cards / 2 cards"
        // flicker on every Refresh. The synchronous ApplyCachedDualLinkState
        // callers have already put the cards in the correct state; this method
        // only replaces that verdict once a fresh one is fully computed.

        var topology = await Task.Run(LinkDiagnosticsService.GetTopology);
        var diag = await Task.Run(() => LinkDiagnosticsService.Collect(monitors));

        // Compute the entire new verdict set off to local variables first.
        // No UI state is mutated while these I/O-bound steps are in flight.
        var newVerdicts = new List<DualLinkVerdict>();
        foreach (var group in topology
                     .Where(t => !string.IsNullOrEmpty(t.Model) && t.Model != "?")
                     .GroupBy(t => t.Model, StringComparer.OrdinalIgnoreCase))
        {
            var names = group.Select(t => t.Device)
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count != 2) continue;

            var techs = names.Where(diag.ContainsKey)
                             .Select(n => diag[n].Technology)
                             .Distinct().ToList();
            if (techs.Count < 2) continue;   // same cable family = two real panels

            // Prefer the link with a live DDC channel (the selected input). If
            // the member set is unchanged from the cached verdict, reuse its
            // primary instead of re-probing — same result, no ~1.5 s VCP stall.
            string? primary = null;
            var namesSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var cached = _dualLinkVerdicts.FirstOrDefault(v =>
                namesSet.SetEquals(v.Names));
            if (cached is not null)
                primary = cached.Primary;

            if (primary is null || !namesSet.Contains(primary))
            {
                primary = null;
                foreach (var name in names)
                {
                    var m = monitors.FirstOrDefault(x =>
                        string.Equals(x.DeviceName, name, StringComparison.OrdinalIgnoreCase));
                    if (m is null) continue;
                    try
                    {
                        if (await _vcpService.GetVcpAsync(m, MonitorInterop.VCP_CODE_BRIGHTNESS) is not null)
                        {
                            primary = name;
                            break;
                        }
                    }
                    catch { /* DDC timeouts are expected on the idle link */ }
                }
                primary ??= names[0];
            }

            newVerdicts.Add(new DualLinkVerdict(group.Key, namesSet, primary));
            LogBrightness($"DUAL LINK merged model={group.Key} links=[{string.Join(", ", names)}] " +
                          $"techs=[{string.Join(",", techs)}] primary={primary} detailsBadge=on");
        }

        // Single atomic swap on the UI thread: publish the new verdicts then
        // apply them in one pass. There is no intermediate "all visible" frame
        // between the reset and the hide, so the card count never flickers.
        _dualLinkVerdicts.Clear();
        _dualLinkVerdicts.AddRange(newVerdicts);
        ApplyCachedDualLinkState();
    }

    /// <summary>
    /// Pushes one monitor's software color (temperature + eye-care level) onto
    /// every other monitor. Called from the details card the user just edited;
    /// a no-op while color sync is off, so per-monitor editing keeps working.
    /// </summary>
    private void BroadcastColor(string sourceDeviceName, int kelvin, int eyeCare)
    {
        if (!IsColorSyncEnabled) return;

        foreach (var detail in MonitorDetails)
        {
            if (string.Equals(detail.Monitor.DeviceName, sourceDeviceName, StringComparison.OrdinalIgnoreCase))
                continue;

            detail.ApplySyncedColor(kelvin, eyeCare);
        }
    }

    // ── Link diagnostics: watchdog + alerts ─────────────────────────────

    /// <summary>
    /// Pushes a fresh link snapshot into every details card. Recomputed after
    /// the cards are built and whenever the watchdog notices the topology has
    /// actually changed (same-model counts moving either way).
    /// </summary>
    private void RefreshLinkDiagnostics()
    {
        try
        {
            if (MonitorDetails.Count == 0) return;
            var monitors = MonitorDetails.Select(c => c.Monitor).ToList();
            var diag = Task.Run(() => LinkDiagnosticsService.Collect(monitors)).GetAwaiter().GetResult();
            foreach (var card in MonitorDetails)
                card.UpdateLink(diag.TryGetValue(card.Monitor.DeviceName, out var d) ? d : null);
        }
        catch (Exception ex)
        {
            LogBrightness($"LINK refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnLinkWatchTick(object? sender, EventArgs e)
    {
        if (_disposed) return;

        List<(string Device, string Model)> topology;
        try
        {
            topology = Task.Run(LinkDiagnosticsService.GetTopology).GetAwaiter().GetResult();
        }
        catch
        {
            return;
        }

        if (_lastTopology is null)
        {
            // First tick: baseline only, never alert.
            _lastTopology = topology;
            return;
        }

        // Compare as model multisets, not raw DISPLAYn names — Windows
        // renumbers the DISPLAYn entries on every plug/unplug, so a naive
        // name diff would cry "lost + gained" for a plain re-arrangement.
        var previous = _lastTopology
            .Where(t => !string.IsNullOrEmpty(t.Model) && t.Model != "?")
            .GroupBy(t => t.Model)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var current = topology
            .Where(t => !string.IsNullOrEmpty(t.Model) && t.Model != "?")
            .GroupBy(t => t.Model)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var changed = previous.Count != current.Count ||
                      previous.Any(kv => !current.TryGetValue(kv.Key, out var c) || c != kv.Value);
        _lastTopology = topology;
        if (!changed) return;

        RefreshLinkDiagnostics();

        // A panel model lost an instance → signal loss.
        foreach (var kv in previous)
        {
            current.TryGetValue(kv.Key, out var now);
            if (now >= kv.Value) continue;
            RaiseLinkAlert("lost:" + kv.Key,
                string.Format(LocalizationService.Get("LD_Alert_SignalLost"), kv.Key));
        }

        // A panel model that just became reachable twice → duplicate links.
        foreach (var kv in current)
        {
            if (kv.Value < 2) continue;
            if (previous.TryGetValue(kv.Key, out var before) && before >= 2) continue;

            var names = string.Join(", ",
                topology.Where(t => string.Equals(t.Model, kv.Key, StringComparison.OrdinalIgnoreCase))
                        .Select(t => t.Device));
            RaiseLinkAlert("dup:" + kv.Key,
                string.Format(LocalizationService.Get("LD_Alert_Duplicate"), kv.Key, kv.Value, names));
        }
    }

    /// <summary>
    /// One modal alert per (kind, model) per cooldown window — a loose cable
    /// that reconnects every few seconds must not turn into a popup loop.
    /// Blocks (modal) on the UI thread by design: the watchdog keeps ticking
    /// but stays quiet for the cooldown window either way.
    /// </summary>
    private void RaiseLinkAlert(string cooldownKey, string text)
    {
        if (_linkAlertCooldown.TryGetValue(cooldownKey, out var last) &&
            DateTime.UtcNow - last < LinkAlertCooldownWindow)
            return;
        _linkAlertCooldown[cooldownKey] = DateTime.UtcNow;
        LogBrightness("LINK ALERT " + cooldownKey);

        var title = LocalizationService.Get("LD_Alert_Title");
        var window = Application.Current?.MainWindow;
        if (window is null)
            MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(window, text, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// Seeds the shared color setting from the primary monitor — the same
    /// "never cause a visible jump out of nowhere" rule brightness sync uses —
    /// then pushes it to the rest.
    /// </summary>
    private void ApplyColorSyncFromPrimary()
    {
        var source = MonitorDetails.FirstOrDefault(m => m.Monitor.IsPrimary)
                     ?? MonitorDetails.FirstOrDefault();
        if (source == null) return;

        BroadcastColor(
            source.Monitor.DeviceName,
            (int)Math.Round(source.ColorTemp),
            (int)Math.Round(source.EyeCare));
    }

    partial void OnIsColorSyncEnabledChanged(bool value)
    {
        if (_isLoading) return;

        if (value) ApplyColorSyncFromPrimary();

        SaveSettings();
    }

    partial void OnBrightnessValueChanged(int value)
    {
        OnPropertyChanged(nameof(SliderValueText));
        if (_applyingBrightness) return;   // programmatic echo of a poll read
        if (!IsBrightnessSyncEnabled || !_brightnessReady) return;
        // Debounce: DDC/CI writes are slow; don't fire one per slider pixel.
        _brightnessApplyTimer.Stop();
        _brightnessApplyTimer.Start();
    }

    partial void OnIsBrightnessSyncEnabledChanged(bool value)
    {
        if (_isLoading) return;

        if (value)
        {
            if (!_brightnessReady) return;
            // Seed the shared value from the primary monitor so enabling sync
            // never causes a brightness jump, then keep them locked.
            _ = Task.Run(async () =>
            {
                await SyncFromSourceAsync(null);
                await _dispatcher.BeginInvoke(() =>
                {
                    if (IsBrightnessSyncEnabled && _brightnessMonitors.Count >= 2)
                        _brightnessPollTimer.Start();
                });
            });
        }
        else
        {
            _brightnessPollTimer.Stop();
            _brightnessApplyTimer.Stop();
        }

        SaveSettings();
    }

    /// <summary>
    /// Re-align immediately when the mode is switched, otherwise the panels stay
    /// on the old (percentage-based) levels until something else moves the slider.
    /// </summary>
    partial void OnAbsoluteBrightnessSyncChanged(bool value)
    {
        if (_isLoading) return;

        // Details block is only meaningful in absolute mode — collapse it
        // when the mode is turned off so the card stays compact.
        if (!value) AbsoluteDetailsExpanded = false;

        SaveSettings();
        OnPropertyChanged(nameof(SliderValueText));
        UpdateBrightnessStatus(_lastBrightnessReads);

        if (IsBrightnessSyncEnabled && _brightnessReady && _brightnessMonitors.Count > 0)
            _ = ApplyToAllAsync(BrightnessValue, "toggle");
    }

    private void PushMappingsToEngine()
    {
        var mappings = Monitors
            .Where(m => m.SelectedAudioDevice != null && !string.IsNullOrEmpty(m.SelectedAudioDevice.Id))
            .Select(m => new MonitorAudioMapping
            {
                MonitorDeviceName = m.Monitor.DeviceName,
                AudioDeviceId = m.SelectedAudioDevice!.Id
            })
            .ToList();

        _routingEngine.UpdateMappings(mappings);
    }

    private void PushOverridesToEngine()
    {
        var dict = AppOverrides.ToDictionary(
            o => o.ProcessName,
            o => o.AudioDeviceId,
            StringComparer.OrdinalIgnoreCase);
        _routingEngine.UpdateAppOverrides(dict);
    }

    /// <summary>
    /// Pin a process to a specific audio device. Replaces any existing pin for
    /// the same process. Persists immediately and pushes to the engine so the
    /// new pin takes effect on the next routing pass (typically &lt;200ms).
    /// </summary>
    /// <param name="icon">
    /// Optional app icon to attach to the pin. Callers that already have the
    /// icon (the right-click flow pins from a live session that's been through
    /// <c>GetProcessIcon</c>) pass it through so the Pinned page shows it
    /// immediately rather than waiting for the next sessions update.
    /// </param>
    public void SetAppOverride(string processName, string deviceId, ImageSource? icon = null)
    {
        if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(deviceId))
            return;

        var deviceName = AudioDevices.FirstOrDefault(d => d.Id == deviceId)?.FriendlyName
                         ?? LocalizationService.Get("Text_UnknownDevice");

        var existing = AppOverrides.FirstOrDefault(
            o => string.Equals(o.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.AudioDeviceId = deviceId;
            existing.AudioDeviceName = deviceName;
            // Only overwrite with a non-null incoming icon — re-pinning from a
            // session preserves the icon we already had if the caller passed null.
            if (icon != null) existing.Icon = icon;
        }
        else
        {
            AppOverrides.Add(new AppOverrideViewModel
            {
                ProcessName = processName,
                AudioDeviceId = deviceId,
                AudioDeviceName = deviceName,
                Icon = icon
            });
        }

        PushOverridesToEngine();
        SaveSettings();
    }

    /// <summary>
    /// Remove the pin for <paramref name="processName"/>. No-op if no pin exists.
    /// The session falls back to the monitor mapping on the next routing pass.
    /// </summary>
    public void RemoveAppOverride(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        var existing = AppOverrides.FirstOrDefault(
            o => string.Equals(o.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return;

        AppOverrides.Remove(existing);
        PushOverridesToEngine();
        SaveSettings();
    }

    /// <summary>
    /// Re-resolve the friendly name of every pin against the current device list.
    /// Called after a hot-plug refresh so a previously-unplugged pinned device
    /// starts showing its real name (and vice versa).
    /// </summary>
    private void RefreshAppOverrideDeviceNames()
    {
        foreach (var ovr in AppOverrides)
        {
            var name = AudioDevices.FirstOrDefault(d => d.Id == ovr.AudioDeviceId)?.FriendlyName;
            ovr.AudioDeviceName = name ?? LocalizationService.Get("Text_DeviceNotConnected");
        }
    }

    /// <summary>
    /// Fills / re-fills the theme dropdown labels from the active string table.
    /// Existing slots are overwritten index-by-index (rather than cleared and
    /// re-added) so the bound SelectedIndex survives a language switch.
    /// </summary>
    private void RefreshThemeOptions()
    {
        var labels = new[]
        {
            LocalizationService.Get("Theme_System"),
            LocalizationService.Get("Theme_Light"),
            LocalizationService.Get("Theme_Dark")
        };

        if (ThemeOptions.Count != labels.Length)
        {
            ThemeOptions.Clear();
            foreach (var label in labels)
                ThemeOptions.Add(label);
            return;
        }

        for (var i = 0; i < labels.Length; i++)
            ThemeOptions[i] = labels[i];
    }

    /// <summary>
    /// Rebuilds every piece of text that isn't fed by a <c>{DynamicResource}</c>
    /// binding: the theme dropdown labels, the version line, the brightness and
    /// update status lines, the "(No mapping)" sentinel in the device dropdown
    /// and the "(Device not connected)" placeholder on pins.
    /// </summary>
    private void OnLanguageChanged()
    {
        if (_disposed) return;

        RefreshThemeOptions();
        OnPropertyChanged(nameof(AppVersionDisplay));

        if (_updateStatusKey.Length > 0)
            UpdateStatus = LocalizationService.Format(_updateStatusKey, _updateStatusArgs);

        UpdateBrightnessStatus(_lastBrightnessReads);
        OnPropertyChanged(nameof(SliderValueText));

        // Peak-nits rows carry the localised "(Primary)" suffix.
        if (_brightnessMonitors.Count > 0)
            RebuildBrightnessPeaks();

        // Monitor titles carry a localised "(Primary)" suffix inside
        // MonitorInfo.DisplayText — re-raise so the bindings re-read it.
        foreach (var monitorVm in Monitors)
            monitorVm.RefreshDisplayText();

        foreach (var detail in MonitorDetails)
            detail.RefreshDisplayTexts();

        RefreshScreenSizeEntryLabels();

        // The device dropdown holds a localised "(No mapping)" entry and
        // AudioDeviceInfo is a plain POCO, so the labels can't be refreshed in
        // place — rebuilding the list is the path that's already proven to
        // preserve every mapping (it re-selects by device ID). Skipped while
        // settings are still loading: that rebuild clears _isLoading and would
        // cut the load-time guard short.
        if (!_isLoading)
            RefreshAudioDevicesPreservingSelections();
    }

    private void SaveSettings()
    {
        var settings = new AppSettings
        {
            IsEnabled = IsRoutingEnabled,
            HoldRoutingWhenWindowHidden = HoldWhenWindowHidden,
            WindowSizeSyncEnabled = IsWindowScaleSyncEnabled,
            WindowPixelWidth = _windowPixelWidth,
            WindowPixelHeight = _windowPixelHeight,
            MonitorWidthCm = new Dictionary<string, double>(_monitorWidthCm, StringComparer.OrdinalIgnoreCase),
            MonitorDiagonalInches = new Dictionary<string, double>(_monitorDiagonalInches, StringComparer.OrdinalIgnoreCase),
            AutoStartWithWindows = AutoStartWithWindows,
            MinimizeOnClose = MinimizeOnClose,
            StartMinimized = StartMinimized,
            IsBrightnessSyncEnabled = IsBrightnessSyncEnabled,
            ColorSyncEnabled = IsColorSyncEnabled,
            AbsoluteBrightnessSync = AbsoluteBrightnessSync,
            MonitorPeakNits = BuildMergedPeakNits(),
            MonitorValidPresets = new Dictionary<string, List<int>>(_validPresets, StringComparer.OrdinalIgnoreCase),
            MonitorColorKelvin = new Dictionary<string, int>(_colorKelvin, StringComparer.OrdinalIgnoreCase),
            MonitorEyeCare = new Dictionary<string, int>(_eyeCare, StringComparer.OrdinalIgnoreCase),
            ThemeMode = SelectedThemeIndex switch { 1 => "Light", 2 => "Dark", _ => "System" },
            Language = LanguageCodeForIndex(SelectedLanguageIndex),
            JumpHotkeyEnabled = IsJumpHotkeyEnabled,
            JumpHotkeyModifiers = JumpHotkeyModifiers,
            JumpHotkeyKey = JumpHotkeyKey,
            ArrangeHotkeyEnabled = IsArrangeHotkeyEnabled,
            AutoArrangeEnabled = IsAutoArrangeEnabled,
            ArrangeHotkeyModifiers = ArrangeHotkeyModifiers,
            ArrangeHotkeyKey = ArrangeHotkeyKey,
            MonitorCustomNames = new Dictionary<string, string>(_monitorNames, StringComparer.OrdinalIgnoreCase),
            MonitorTags = new Dictionary<string, string>(_monitorTags, StringComparer.OrdinalIgnoreCase),
            // Persist user intent (DesiredAudioDeviceId), not the live SelectedAudioDevice.
            // Otherwise a Save triggered while a device is temporarily unplugged
            // (e.g. the user toggles AutoStartWithWindows during a hot-unplug)
            // would write that mapping out of the file permanently.
            Mappings = Monitors
                .Where(m => !string.IsNullOrEmpty(m.DesiredAudioDeviceId))
                .Select(m => new MonitorAudioMapping
                {
                    MonitorDeviceName = m.Monitor.DeviceName,
                    AudioDeviceId = m.DesiredAudioDeviceId
                })
                .ToList(),
            // Pins survive device unplug for the same reason mappings do — we
            // store the device ID, not its present-or-not state.
            AppOverrides = AppOverrides.ToDictionary(
                o => o.ProcessName,
                o => o.AudioDeviceId,
                StringComparer.OrdinalIgnoreCase)
        };

        _settingsService.Save(settings);
    }

    partial void OnIsRoutingEnabledChanged(bool value)
    {
        if (value)
        {
            PushMappingsToEngine();
            _routingEngine.Start();
        }
        else
        {
            _routingEngine.Stop();
            ActiveSessions.Clear();
        }

        if (!_isLoading)
            SaveSettings();
    }

    /// <summary>
    /// Pushes the "window hidden → keep the audio device" switch into the engine.
    /// The engine re-evaluates immediately, so turning the option off releases
    /// apps that are currently being held.
    /// </summary>
    partial void OnHoldWhenWindowHiddenChanged(bool value)
    {
        _routingEngine.SetHoldWhenWindowHidden(value);

        if (!_isLoading)
            SaveSettings();
    }

    partial void OnIsWindowScaleSyncEnabledChanged(bool value)
    {
        if (!_isLoading)
            SaveSettings();
    }

    partial void OnAutoStartWithWindowsChanged(bool value)
    {
        if (!_isLoading)
        {
            ApplyAutoStart(value);
            SaveSettings();
        }
    }

    partial void OnMinimizeOnCloseChanged(bool value)
    {
        if (!_isLoading)
            SaveSettings();
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (!_isLoading)
            SaveSettings();
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        if (!_isLoading)
            SaveSettings();
    }

    /// <summary>
    /// Swaps the string table live. Every <c>{DynamicResource}</c> label follows
    /// immediately; the code-generated text is refreshed via the
    /// <see cref="LocalizationService.LanguageChanged"/> handler, which
    /// <see cref="LocalizationService.Apply"/> raises before we persist.
    /// </summary>
    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (_isLoading) return;

        LocalizationService.Apply(LanguageCodeForIndex(value));

        SaveSettings();
    }

    private void ApplyAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistryKey, true);
            if (key == null) return;

            key.DeleteValue(LegacyAppRegistryName, false);

            if (enable)
            {
                var exePath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                    key.SetValue(AppRegistryName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppRegistryName, false);
            }
        }
        catch { }
    }

    // ── About page: check for updates ────────────────────────────────────
    //
    // User clicks the button → we hit the GitHub Releases API, compare tags,
    // and write a one-line result into UpdateStatus. LatestReleaseUrl is set
    // only when an update is available; the XAML uses it to decide whether to
    // render the "Download" hyperlink. IsCheckingForUpdate gates the button so
    // a double-click can't fire two concurrent probes.

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdate) return;

        IsCheckingForUpdate = true;
        SetUpdateStatus("Update_Checking");
        LatestReleaseUrl = string.Empty;

        try
        {
            var result = await _updateService.CheckForUpdateAsync();
            switch (result)
            {
                case UpdateCheckResult.UpToDate upToDate:
                    SetUpdateStatus("Update_UpToDate", upToDate.CurrentVersion);
                    break;
                case UpdateCheckResult.UpdateAvailable update:
                    SetUpdateStatus("Update_Available", update.LatestVersion);
                    LatestReleaseUrl = update.ReleaseUrl;
                    break;
                case UpdateCheckResult.NetworkError net:
                    SetUpdateStatus("Update_NetworkError", net.Message);
                    break;
                case UpdateCheckResult.Failed fail:
                    SetUpdateStatus("Update_Failed", fail.Message);
                    break;
            }
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        var wasRunning = IsRoutingEnabled;
        if (wasRunning)
            _routingEngine.Stop();

        // Snapshot intent (DesiredAudioDeviceId) rather than current resolved
        // selection — a missing device's mapping should survive a manual
        // refresh the same way it survives a hot-plug refresh.
        var desiredByMonitor = Monitors.ToDictionary(
            m => m.Monitor.DeviceName,
            m => m.DesiredAudioDeviceId);

        LoadDevicesAndMonitors();

        // Drop ghost twin links of dual-link panels using the IDs the user has
        // saved (LoadDevicesAndMonitors rebuilt the list from raw endpoints).
        RebuildAudioDeviceList();

        // Re-probe brightness channels — monitor topology may have changed
        // (hot-plug, resolution change), and physical monitor handles go stale.
        _ = InitializeBrightnessAsync();

        _isLoading = true;
        try
        {
            foreach (var monitorVm in Monitors)
            {
                if (!desiredByMonitor.TryGetValue(monitorVm.Monitor.DeviceName, out var desiredId))
                    continue;

                // Re-seed the desired ID first — LoadDevicesAndMonitors built
                // brand-new MonitorMappingViewModel instances with empty defaults.
                monitorVm.DesiredAudioDeviceId = desiredId;
                if (monitorVm.IsPrimaryDisplay)
                {
                    monitorVm.SuspendBlankGuard = true;
                    try { monitorVm.SetDeviceProgrammatically(
                        ResolvePrimaryTarget(desiredId, RealDevicesInDropdown())); }
                    finally { monitorVm.SuspendBlankGuard = false; }
                }
                else
                {
                    monitorVm.SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == desiredId);
                }
            }

            // OnMappingChanged is suppressed under _isLoading, so read volumes
            // explicitly — sliders would otherwise show their default of 100.
            RefreshAssignedVolumes();
        }
        finally
        {
            _isLoading = false;
        }

        // Pin device names are derived from AudioDevices — refresh them after
        // the device list rebuild so the Pinned page reflects the new state.
        RefreshAppOverrideDeviceNames();

        if (wasRunning)
        {
            PushMappingsToEngine();
            PushOverridesToEngine();
            _routingEngine.Start();
            _isLoading = true;
            IsRoutingEnabled = true;
            _isLoading = false;
        }
    }

    private ImageSource? GetProcessIcon(uint processId)
    {
        if (_iconCache.TryGetValue(processId, out var cached))
            return cached;

        ImageSource? icon = null;
        try
        {
            var process = Process.GetProcessById((int)processId);
            var path = process.MainModule?.FileName;
            if (path != null)
            {
                using var drawingIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (drawingIcon != null)
                {
                    icon = Imaging.CreateBitmapSourceFromHIcon(
                        drawingIcon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    icon.Freeze();
                }
            }
        }
        catch { }

        _iconCache[processId] = icon;
        return icon;
    }

    private void OnSessionsUpdated(List<AudioSessionInfo> sessions)
    {
        var sessionData = sessions.Select(s => new
        {
            Session = s,
            Icon = GetProcessIcon(s.ProcessId)
        }).ToList();

        _dispatcher.BeginInvoke(() =>
        {
            ActiveSessions.Clear();
            foreach (var item in sessionData)
            {
                ActiveSessions.Add(new SessionDisplayViewModel
                {
                    ProcessName = item.Session.ProcessName,
                    ProcessId = item.Session.ProcessId,
                    MonitorName = item.Session.MonitorFriendlyName,
                    AudioDeviceName = item.Session.AudioDeviceName,
                    Icon = item.Icon,
                    IsOverridden = item.Session.IsOverridden
                });
            }

            // Back-fill icons on pins loaded from disk: when a pinned app first
            // starts playing audio we now have a PID to extract its icon from,
            // so the Pinned page switches from the pin-glyph fallback to the real
            // app icon. Matches are case-insensitive; first live session wins.
            foreach (var ovr in AppOverrides)
            {
                if (ovr.Icon != null) continue;
                var match = sessionData.FirstOrDefault(s =>
                    string.Equals(s.Session.ProcessName, ovr.ProcessName,
                                  StringComparison.OrdinalIgnoreCase)
                    && s.Icon != null);
                if (match != null)
                    ovr.Icon = match.Icon;
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        LocalizationService.LanguageChanged -= OnLanguageChanged;
        _vcpService.Dispose();
        _linkWatchTimer.Stop();

        // Unsubscribe + dispose the notifier BEFORE stopping the timer so any
        // late COM-thread callback that was already queued observes _disposed
        // and bails instead of kicking the timer from a background thread.
        _deviceNotifier.DevicesChanged -= OnDeviceChangeDetected;
        _deviceNotifier.DefaultDeviceChanged -= OnDeviceChangeDetected;
        _deviceNotifier.Dispose();

        _deviceRefreshTimer.Stop();
        _deviceRefreshTimer.Tick -= OnDeviceRefreshTimerTick;

        _brightnessPollTimer.Stop();
        _brightnessPollTimer.Tick -= OnBrightnessPollTick;
        _brightnessApplyTimer.Stop();
        _brightnessApplyTimer.Tick -= OnBrightnessApplyTimerTick;
        _brightnessService.Dispose();

        _routingEngine.SessionsUpdated -= OnSessionsUpdated;

        // Reset all audio routing back to system defaults
        _routingEngine.ResetAllRouting();

        _routingEngine.Dispose();
        _routerService.Dispose();
        _audioDeviceService.Dispose();

        // Restore every gamma ramp we tinted. Windows only resets ramps at
        // logoff, so without this a user who closes the app (or uninstalls it)
        // would be left staring at a warm/blue-shifted desktop with no obvious
        // way back. Runs on the real-exit path only: minimize-to-tray never
        // reaches Dispose, so the tint stays put while the app lives in the
        // tray — which is exactly when the user still wants it.
        RestoreAllGammaRamps();
    }

    private void RestoreAllGammaRamps()
    {
        foreach (var deviceName in _colorKelvin.Keys.Union(_eyeCare.Keys).Distinct())
        {
            var kelvin = _colorKelvin.TryGetValue(deviceName, out var k) ? k : ColorGainService.NeutralKelvin;
            var eye = _eyeCare.TryGetValue(deviceName, out var e) ? e : 0;
            if (kelvin == ColorGainService.NeutralKelvin && eye == 0) continue;

            try { ColorGainService.Reset(deviceName); }
            catch { /* exiting — a stuck ramp on one screen must not block the rest */ }
        }
    }
}
