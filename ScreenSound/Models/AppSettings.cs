namespace ScreenSound.Models;

public class AppSettings
{
    public List<MonitorAudioMapping> Mappings { get; set; } = new();
    public bool IsEnabled { get; set; } = true;
    public bool AutoStartWithWindows { get; set; }
    public bool MinimizeOnClose { get; set; }
    public bool StartMinimized { get; set; }
    public string ThemeMode { get; set; } = "System"; // "System", "Light", "Dark"

    /// <summary>
    /// UI language: "en" or "zh". Empty (the default for settings files written
    /// by older builds) means "pick from the OS UI language" — see
    /// <c>LocalizationService.Normalize</c>.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Per-app override pins: process name (without .exe, case-insensitive) → audio device ID.
    /// When present, an entry takes precedence over the monitor → device mapping for that process,
    /// regardless of which monitor its window happens to be on. Survives device hot-plug and
    /// window moves; only cleared when the user explicitly removes the pin.
    /// </summary>
    public Dictionary<string, string> AppOverrides { get; set; } = new();

    /// <summary>
    /// When an app's window can't be found on any monitor (hidden to tray,
    /// minimised, or an app that hides its UI during a call), keep its audio on
    /// the device it was last routed to instead of releasing it back to the
    /// Windows default. Defaults to true — releasing is what caused call audio
    /// to jump to another monitor's speaker mid-call.
    /// </summary>
    public bool HoldRoutingWhenWindowHidden { get; set; } = true;

    /// <summary>
    /// Keep the main window at the same physical size when it's dragged to a
    /// monitor with a different DPI scale. Windows (and WPF) preserve the
    /// *logical* size instead, so a 900 DIP window jumps from 1350 px to
    /// 1800 px wide when it crosses from a 150 % panel to a 200 % one and
    /// suddenly looks much bigger. "Physical" here means centimetres: the
    /// window is resized through each monitor's real pixel density (see
    /// <see cref="MonitorWidthCm"/>) so it measures the same with a ruler on
    /// every screen, not merely the same pixel count.
    /// </summary>
    public bool WindowSizeSyncEnabled { get; set; } = true;

    /// <summary>
    /// Measured viewable width per monitor, keyed by the monitor model from
    /// its device ID (e.g. "HIV0160" → 29.4). EDID self-reports the physical
    /// size, but some panels (portable monitors especially) round up or reuse
    /// a template block, which makes the reported density wrong by 10–20 %.
    /// A tape-measured entry here overrides EDID and is what makes
    /// <see cref="WindowSizeSyncEnabled"/> ruler-accurate. Missing entries
    /// fall back to EDID.
    /// </summary>
    public Dictionary<string, double> MonitorWidthCm { get; set; } = new();

    /// <summary>
    /// Viewable DIAGONAL size in inches per monitor model (e.g. "HIV0160" → 14),
    /// keyed the same way as <see cref="MonitorWidthCm"/> so both cables of a
    /// dual-link panel share one value. The user enters the number printed on
    /// the screen's box; combined with the resolution Windows reports, the app
    /// derives real PPI as sqrt(w²+h²) / inches — no ruler, no EDID (which some
    /// portable panels fake by 10–20 %) and no hardcoded model table needed.
    /// Takes precedence over <see cref="MonitorWidthCm"/> and EDID in
    /// <see cref="ScreenSound.Services.WindowScaleService.GetRealPpi"/>.
    /// </summary>
    public Dictionary<string, double> MonitorDiagonalInches { get; set; } = new();

    /// <summary>
    /// Physical size of the main window (pixels) as of the last shutdown.
    /// Re-applied on the next launch so restarting on a different-DPI monitor
    /// doesn't change the window's size either. 0 = never recorded.
    /// </summary>
    public int WindowPixelWidth { get; set; }
    public int WindowPixelHeight { get; set; }

    /// <summary>
    /// Brightness sync: keep every monitor at the same brightness level.
    /// External monitors are driven via DDC/CI, integrated panels via WMI.
    /// </summary>
    public bool IsBrightnessSyncEnabled { get; set; }

    /// <summary>
    /// Absolute brightness sync. When false, panels are aligned on their 0–100 %
    /// setting, which looks uneven whenever the panels have different peak
    /// luminance (a 400-nit panel at 50 % is visibly dimmer than a 600-nit one).
    /// When true, the percentage is converted through each panel's peak nits so
    /// every screen lands on the same real luminance.
    /// </summary>
    public bool AbsoluteBrightnessSync { get; set; }

    /// <summary>
    /// Peak luminance per monitor (Monitor.DeviceName → nits), entered by the
    /// user because DDC/CI has no standard "report your nits" VCP code. Used
    /// only when <see cref="AbsoluteBrightnessSync"/> is on. Missing entries
    /// fall back to a neutral 250 nits so unknown panels stay proportional.
    /// </summary>
    public Dictionary<string, int> MonitorPeakNits { get; set; } = new();

    /// <summary>
    /// Color-preset values a panel actually accepts (Monitor.DeviceName → list).
    /// Cheap panels advertise e.g. 13 presets but snap all but three back, so
    /// the dropdown is built from this validated set. Cached because the probe
    /// writes every candidate once (visible flicker) and is not worth repeating.
    /// </summary>
    public Dictionary<string, List<int>> MonitorValidPresets { get; set; } = new();

    /// <summary>Software color temperature per monitor (Kelvin), 6500 = untouched.</summary>
    public Dictionary<string, int> MonitorColorKelvin { get; set; } = new();

    /// <summary>Software low-blue-light level per monitor (0–100).</summary>
    public Dictionary<string, int> MonitorEyeCare { get; set; } = new();

    /// <summary>
    /// Software color sync: every monitor shares one color temperature and
    /// eye-care level, so the whole desk looks the same. Editable from any
    /// monitor's card on the screen-details page — that card's values are pushed
    /// to the others. Mirrors the brightness-sync switch, which does the same for
    /// luminance instead of tint.
    /// </summary>
    public bool ColorSyncEnabled { get; set; }

    /// <summary>
    /// Global "jump window" hotkey: sends the foreground window to the other
    /// display (centered; maximized windows fill the target screen). Enabled by
    /// default so the feature works out of the box after install.
    /// </summary>
    public bool JumpHotkeyEnabled { get; set; } = true;

    /// <summary>
    /// Modifier flags for the jump hotkey, using RegisterHotKey's MOD_* values
    /// (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8).
    /// Defaults to Alt (1): a modifier combo can never be a typed
    /// character, so the hotkey is always available and never swallows a
    /// letter. A bare key (0) is still allowed — the typing guard releases it
    /// while the foreground app owns a text caret.
    /// </summary>
    public int JumpHotkeyModifiers { get; set; } = 1;

    /// <summary>
    /// Virtual-key code for the jump hotkey. Defaults to 0x46 = 'F'.
    /// </summary>
    public int JumpHotkeyKey { get; set; } = 0x46;

    /// <summary>
    /// Global "tile windows" hotkey: arranges every window on the secondary
    /// display into an even Snap-like layout (one fills the screen, two split
    /// left/right on a landscape panel or top/bottom on a portrait one).
    /// Defaults to 0x55 = 'U'; a value &lt;= 0 disables the binding.
    /// </summary>
    public int ArrangeHotkeyKey { get; set; } = 0x55;

    /// <summary>
    /// MOD_* modifier bits for the tile hotkey. Defaults to Alt (1) for
    /// the same reason as <see cref="JumpHotkeyModifiers"/>: typing safety.
    /// </summary>
    public int ArrangeHotkeyModifiers { get; set; } = 1;

    /// <summary>
    /// Master switch for the U (tile layout) hotkey. On by default so
    /// existing installs keep the manual shortcut after upgrading. This is
    /// independent of <see cref="AutoArrangeEnabled"/>: turning the hotkey
    /// off does not stop automatic tiling, and vice versa.
    /// </summary>
    public bool ArrangeHotkeyEnabled { get; set; } = true;
    /// <summary>
    /// Automatically tile the secondary display whenever a new window arrives
    /// or a window is shrunk leaving free space — no key press needed. Off by
    /// default: opt-in, so enabling it is a deliberate choice.
    /// </summary>
    public bool AutoArrangeEnabled { get; set; }

    /// <summary>
    /// User-chosen display names, keyed by the monitor's identity key
    /// (MonitorInfo.IdentityKey — a per-unit device interface id). Missing
    /// entries fall back to the name Windows reports.
    /// </summary>
    public Dictionary<string, string> MonitorCustomNames { get; set; } = new();

    /// <summary>
    /// Free-form labels ("工作屏" / "便携屏") shown as chips next to the name,
    /// keyed the same way as <see cref="MonitorCustomNames"/>.
    /// </summary>
    public Dictionary<string, string> MonitorTags { get; set; } = new();
}

public class MonitorAudioMapping
{
    public string MonitorDeviceName { get; set; } = string.Empty;
    public string AudioDeviceId { get; set; } = string.Empty;
}
