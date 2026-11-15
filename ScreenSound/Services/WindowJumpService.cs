using ScreenSound.Interop;

namespace ScreenSound.Services;

/// <summary>
/// Implements the global "jump window" hotkey: takes the currently focused
/// window and relocates it to the *other* display.
///
/// "Other" means: a window on the primary display goes to the first
/// non-primary display, and a window anywhere else comes back to the primary.
/// That gives a natural two-screen toggle — press once to throw the window to
/// the portable screen, press again to bring it back — and still does
/// something sensible on 3+ display setups.
///
/// Normal windows are centred inside the target display's *work* area (so the
/// taskbar never overlaps the result); their pixel size is preserved.
/// Maximised windows are re-maximised onto the target display instead —
/// SetWindowPos with the target's full monitor rectangle keeps them pinned to
/// the new screen, where re-centring a "full screen" window is meaningless.
/// Minimised windows are restored first: a minimised HWND is parked at
/// -32000,-32000 and has no display to resolve "the other screen" from.
/// </summary>
public static class WindowJumpService
{
    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    /// <summary>
    /// Moves the foreground window to the other display, centred (or
    /// maximised, if it was maximised). No-ops when there is only one display
    /// or when no window can be resolved — a silent no-op beats throwing a
    /// native error at the user for what is a convenience feature.
    /// </summary>
    public static void MoveForegroundToOtherMonitor()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        // GetForegroundWindow can return a child HWND (e.g. a panel inside an
        // Electron app). Position must be set on the top-level window.
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root != IntPtr.Zero) hwnd = root;

        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        var current = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitors = EnumerateMonitors();
        if (monitors.Count < 2) return;

        // Pick the target: primary if the window already left it, otherwise
        // the first non-primary display (the "secondary screen").
        (IntPtr hMonitor, NativeMethods.MONITORINFOEX info)? target = null;
        foreach (var (hMonitor, mInfo) in monitors)
        {
            if (hMonitor == current) continue;
            if ((mInfo.dwFlags & MONITORINFOF_PRIMARY) != 0)
            {
                target = (hMonitor, mInfo);
                break;
            }
            target ??= (hMonitor, mInfo);
        }
        if (target == null || target.Value.hMonitor == current) return;

        var info = target.Value.info;

        if (NativeMethods.IsZoomed(hwnd))
        {
            // Re-maximise onto the target screen: move to its monitor rectangle
            // and adopt its size, so a maximised window keeps filling the new
            // display even when the two panels differ in resolution.
            var m = info.rcMonitor;
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            return;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var work = info.rcWork;

        // Centre on the work area, clamped so the window stays fully visible
        // (a window larger than the work area pins to its top-left corner
        // rather than losing its caption bar off-screen).
        var x = work.Left + ((work.Right - work.Left) - width) / 2;
        var y = work.Top + ((work.Bottom - work.Top) - height) / 2;
        x = Math.Max(work.Left, Math.Min(x, work.Right - width));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));

        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
            x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// All connected displays with their full monitor and work rectangles.
    /// Snapshotted per call — the hotkey fires rarely, and a fresh enumeration
    /// is the only way to handle a display plugged in since the last jump.
    /// Public so the layout service can share the exact same "which screen is
    /// the secondary one" snapshot instead of drifting into its own copy.
    /// </summary>
    public static List<(IntPtr hMonitor, NativeMethods.MONITORINFOEX info)> EnumerateMonitors()
    {
        var result = new List<(IntPtr, NativeMethods.MONITORINFOEX)>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT rect, IntPtr dwData) =>
            {
                var info = new NativeMethods.MONITORINFOEX();
                info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>();
                if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
                    result.Add((hMonitor, info));
                return true;
            }, IntPtr.Zero);
        return result;
    }
}
