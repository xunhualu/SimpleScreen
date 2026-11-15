using ScreenSound.Interop;

namespace ScreenSound.Services;

/// <summary>
/// Event-driven state machine behind the "Auto-optimise layout" switch.
///
/// The rule the feature is built around: optimise ONCE, then leave the user
/// alone. Re-optimising on every window move would fight anyone who drags the
/// divider to a 30/70 split, so the watcher keeps a *baseline* — the set of
/// windows present at the last optimisation — and only acts again when
///
///   • a window that wasn't in the baseline appears (a new page dragged in), or
///   • the windows no longer cover the work area (one was shrunk or dragged
///     away, leaving free space).
///
/// Changing the split ratio keeps coverage at ~100 % and the window set
/// unchanged, so it never re-triggers. Both conditions are deliberately
/// independent: a new window dropped on top of a full-screen one still has
/// full coverage, but must still be tiled in.
///
/// A short settle delay plus a left-button check keeps us from yanking a
/// window out from under the mouse mid-drag.
/// </summary>
public sealed class AutoArrangeWatcher
{
    /// <summary>Below this union coverage the screen counts as "has free space".</summary>
    private const double MinCoverage = 0.80;

    /// <summary>How long the layout must stop changing before we act.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(650);

    /// <summary>Quiet period right after our own arrangement.</summary>
    private static readonly TimeSpan PostArrangeCooldown = TimeSpan.FromMilliseconds(900);

    private HashSet<IntPtr> _baseline = new();
    private bool _hasBaseline;
    private NativeMethods.RECT? _targetArea;
    private string _lastSignature = string.Empty;
    private DateTime _lastChangeUtc = DateTime.MinValue;
    private DateTime _lastArrangeUtc = DateTime.MinValue;

    public bool Enabled { get; private set; }

    /// <summary>Turns the watcher on/off. Enabling arranges once immediately.</summary>
    public void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        Reset();

        if (!enabled) return;

        // Opening the switch should visibly do something right away, and
        // establishes the baseline from which "no more auto-optimising" is
        // measured.
        var result = WindowLayoutService.Arrange(_targetArea);
        _targetArea = result.Area;
        EstablishBaseline(result.Area);
        _lastArrangeUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Manual trigger (the U hotkey). Always arranges, regardless of the
    /// baseline, and re-arms the baseline so the next auto check starts clean.
    /// </summary>
    public void ArrangeNow()
    {
        var result = WindowLayoutService.Arrange(_targetArea);
        _targetArea = result.Area;
        EstablishBaseline(result.Area);
        _lastArrangeUtc = DateTime.UtcNow;
        _lastSignature = string.Empty;
    }

    /// <summary>Called on a ~600 ms dispatcher timer while the switch is on.</summary>
    public void Tick()
    {
        if (!Enabled) return;

        // Dragging or resizing with the mouse held down: don't touch anything,
        // and don't refresh the settle clock either. The first quiet tick after
        // release starts the settle countdown.
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0) return;

        var area = WindowLayoutService.GetTargetWorkArea(_targetArea);
        _targetArea = area;
        var windows = WindowLayoutService.GetWindowsOn(area);

        var signature = Signature(windows);
        var now = DateTime.UtcNow;
        if (signature != _lastSignature)
        {
            _lastSignature = signature;
            _lastChangeUtc = now;
        }

        if (windows.Count == 0)
        {
            _hasBaseline = false;
            _baseline.Clear();
            return;
        }

        if (now - _lastChangeUtc < SettleTime) return;
        if (now - _lastArrangeUtc < PostArrangeCooldown) return;

        if (!_hasBaseline)
        {
            // The display was empty and windows have (re)appeared.
            ArrangeAndArm(area, now);
            return;
        }

        var newWindow = windows.Any(w => !_baseline.Contains(w.Handle));
        var coverage = WindowLayoutService.Coverage(area, windows);
        if (!newWindow && coverage >= MinCoverage) return;

        ArrangeAndArm(area, now);
    }

    private void ArrangeAndArm(NativeMethods.RECT area, DateTime now)
    {
        var result = WindowLayoutService.Arrange(area);
        _targetArea = result.Area;
        EstablishBaseline(result.Area);
        _lastArrangeUtc = now;
        _lastSignature = string.Empty;
    }

    private void EstablishBaseline(NativeMethods.RECT area)
    {
        _baseline = WindowLayoutService.GetWindowsOn(area)
            .Select(w => w.Handle)
            .ToHashSet();
        _hasBaseline = true;
    }

    private void Reset()
    {
        _baseline.Clear();
        _hasBaseline = false;
        _lastSignature = string.Empty;
        _lastChangeUtc = DateTime.MinValue;
        _lastArrangeUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Quantised geometry fingerprint. Quantising to 12 px avoids treating
    /// sub-pixel DWM jitter as a layout change, while any real move/resize
    /// still registers.
    /// </summary>
    private static string Signature(IEnumerable<LayoutWindow> windows) =>
        string.Join("|", windows
            .OrderBy(w => w.Handle.ToInt64())
            .Select(w => $"{w.Handle.ToInt64():x}:" +
                         $"{w.Rect.Left / 12},{w.Rect.Top / 12}," +
                         $"{w.Rect.Right / 12},{w.Rect.Bottom / 12}"));
}
