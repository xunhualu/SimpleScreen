using System.Runtime.InteropServices;
using System.Text;
using ScreenSound.Interop;

namespace ScreenSound.Services;

/// <summary>A real, movable top-level window found on the target display.</summary>
public sealed class LayoutWindow
{
    public required IntPtr Handle { get; init; }
    public required NativeMethods.RECT Rect { get; init; }
    public int CenterX => (Rect.Left + Rect.Right) / 2;
    public int CenterY => (Rect.Top + Rect.Bottom) / 2;
}

/// <summary>Return value of <see cref="WindowLayoutService.Arrange"/>.</summary>
public readonly record struct ArrangeResult(NativeMethods.RECT Area, int WindowCount);

/// <summary>
/// Tiles the windows on the secondary display the way Windows Snap does:
/// one window fills the work area, two split left/right on a landscape panel
/// (or top/bottom on a rotated/portrait one), three get a large half plus two
/// quarter panes, four and more get an even grid.
///
/// All geometry is in *physical pixels* — the process is Per-Monitor-V2 DPI
/// aware, so GetWindowRect / SetWindowPos already speak the same coordinate
/// space even when the two panels run at different scaling.
/// </summary>
public static class WindowLayoutService
{
    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    /// <summary>
    /// Ignore anything smaller than this fraction of the work area. Tool
    /// palettes, login popups and desktop widgets all show up in EnumWindows;
    /// tiling one of them would wreck the user's layout. A freshly dragged-in
    /// browser/editor window is comfortably above the threshold.
    /// </summary>
    private const double MinWindowAreaRatio = 0.045;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Shell_SecondaryNotifyIconWnd", "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow", "TaskListWindow",
        "Windows.UI.Composition.DesktopWindowContentBridge", "XamlExplorerHostIslandWindow",
        "tooltips_class32", "Button", "#32768",
    };

    /// <summary>
    /// Chooses which display to tile. Preference order: a non-primary display
    /// (the "secondary screen" the jump hotkey also targets), sticking to the
    /// previously used rectangle when it still exists, otherwise the
    /// non-primary display that currently holds the most windows.
    ///
    /// HARD RULE: the primary display is never a candidate. If no secondary
    /// panel is visible (unplugged, lid closed, clone/duplicate mode) this
    /// returns an empty rectangle, which makes Arrange() and the auto watcher
    /// complete no-ops rather than silently re-tiling the user's main screen.
    ///
    /// The sticky rectangle matters on setups where Windows exposes two paths
    /// to the same portable panel (HDMI + USB-C DP Alt): without it the target
    /// could flip between ticks and re-tile for no reason.
    /// </summary>
    public static NativeMethods.RECT GetTargetWorkArea(NativeMethods.RECT? sticky = null)
    {
        var monitors = WindowJumpService.EnumerateMonitors();
        if (monitors.Count == 0) return default;

        var candidates = monitors
            .Where(m => (m.info.dwFlags & MONITORINFOF_PRIMARY) == 0)
            .ToList();
        if (candidates.Count == 0) return default;

        if (sticky.HasValue)
        {
            foreach (var m in candidates)
                if (SameRect(m.info.rcWork, sticky.Value))
                    return m.info.rcWork;
        }

        NativeMethods.RECT best = candidates[0].info.rcWork;
        var bestCount = -1;
        foreach (var m in candidates)
        {
            var count = GetWindowsOn(m.info.rcWork).Count;
            if (count > bestCount)
            {
                bestCount = count;
                best = m.info.rcWork;
            }
        }
        return best;
    }

    /// <summary>
    /// Visible, movable app windows whose centre lies inside <paramref name="area"/>.
    /// Filters out our own window (the hotkey fires while SimpleScreen itself
    /// is focused), the shell, tool windows, cloaked UWP windows and tiny
    /// popups. A window straddling two displays is attributed by its centre —
    /// exactly how Windows decides where to Snap a window.
    /// </summary>
    public static List<LayoutWindow> GetWindowsOn(NativeMethods.RECT area)
    {
        var result = new List<LayoutWindow>();
        var areaWidth = area.Right - area.Left;
        var areaHeight = area.Bottom - area.Top;
        if (areaWidth <= 0 || areaHeight <= 0) return result;

        var selfPid = (uint)Environment.ProcessId;
        double minArea = (double)areaWidth * areaHeight * MinWindowAreaRatio;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                if (NativeMethods.IsIconic(hwnd)) return true;

                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == selfPid) return true;

                var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
                if ((exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0) return true;

                var sb = new StringBuilder(256);
                if (NativeMethods.GetClassName(hwnd, sb, sb.Capacity) > 0 && ShellClasses.Contains(sb.ToString()))
                    return true;

                // Suspended UWP/WinUI shells remain "visible" to EnumWindows
                // but are not really on screen.
                if (NativeMethods.DwmGetWindowAttributeInt(hwnd, NativeMethods.DWMWA_CLOAKED,
                        out var cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;

                if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return true;
                var w = rect.Right - rect.Left;
                var h = rect.Bottom - rect.Top;
                if (w < 160 || h < 120) return true;
                if ((double)w * h < minArea) return true;

                var cx = (rect.Left + rect.Right) / 2;
                var cy = (rect.Top + rect.Bottom) / 2;
                if (cx < area.Left || cx >= area.Right || cy < area.Top || cy >= area.Bottom)
                    return true;

                result.Add(new LayoutWindow { Handle = hwnd, Rect = rect });
            }
            catch
            {
                // A window can vanish between enumeration and inspection.
                // Never let one bad HWND abort the whole callback.
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// Fraction of the work area covered by the *union* of the windows
    /// (overlapping windows are not double-counted). Uses a boundary sweep
    /// line, so it is exact for axis-aligned rectangles rather than a
    /// pixel-sampling approximation.
    /// </summary>
    public static double Coverage(NativeMethods.RECT area, IReadOnlyList<LayoutWindow> windows)
    {
        var aw = area.Right - area.Left;
        var ah = area.Bottom - area.Top;
        if (aw <= 0 || ah <= 0 || windows.Count == 0) return 0;

        var rects = new List<NativeMethods.RECT>();
        foreach (var w in windows)
        {
            var r = Clamp(w.Rect, area);
            if (r.Right > r.Left && r.Bottom > r.Top) rects.Add(r);
        }
        if (rects.Count == 0) return 0;

        var xs = new SortedSet<int> { area.Left, area.Right };
        var ys = new SortedSet<int> { area.Top, area.Bottom };
        foreach (var r in rects)
        {
            xs.Add(r.Left); xs.Add(r.Right);
            ys.Add(r.Top); ys.Add(r.Bottom);
        }
        var xa = xs.ToArray();
        var ya = ys.ToArray();

        double covered = 0;
        for (var i = 0; i < xa.Length - 1; i++)
        {
            var cx = xa[i] + (xa[i + 1] - xa[i]) / 2;
            for (var j = 0; j < ya.Length - 1; j++)
            {
                var cy = ya[j] + (ya[j + 1] - ya[j]) / 2;
                var hit = false;
                foreach (var r in rects)
                {
                    if (cx >= r.Left && cx < r.Right && cy >= r.Top && cy < r.Bottom)
                    {
                        hit = true;
                        break;
                    }
                }
                if (hit)
                    covered += (double)(xa[i + 1] - xa[i]) * (ya[j + 1] - ya[j]);
            }
        }
        return Math.Min(1.0, covered / ((double)aw * ah));
    }

    /// <summary>
    /// Tiles every window on the target display. Returns the area actually
    /// used so the auto-arrange watcher can keep its sticky target and its
    /// baseline in the same coordinate space.
    /// </summary>
    public static ArrangeResult Arrange(NativeMethods.RECT? sticky = null)
    {
        var area = GetTargetWorkArea(sticky);
        if (area.Right - area.Left <= 0 || area.Bottom - area.Top <= 0)
            return new ArrangeResult(area, 0);

        var windows = GetWindowsOn(area);
        if (windows.Count == 0) return new ArrangeResult(area, 0);

        var landscape = (area.Right - area.Left) >= (area.Bottom - area.Top);
        SortForReadingOrder(windows, landscape);

        // Keep the pane count sane — a desktop with 40 windows shouldn't turn
        // into 40 unreadable slivers. Extra windows are left where they are.
        var count = Math.Min(windows.Count, 8);
        var slots = BuildSlots(area, count, landscape);

        for (var i = 0; i < count; i++)
        {
            var hwnd = windows[i].Handle;
            if (!NativeMethods.IsWindow(hwnd)) continue;

            if (NativeMethods.IsZoomed(hwnd))
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

            // GetWindowRect includes the invisible resize/shadow border that
            // DWM adds on Windows 10/11. SetWindowPos positions that *outer*
            // rectangle, so without compensating, two half-screen windows show
            // a ~14 px gutter between them and sit a few pixels off-screen.
            NativeMethods.GetWindowRect(hwnd, out var outer);
            var inset = GetFrameInsets(hwnd, outer);
            var slot = slots[i];

            var x = slot.Left - inset.Left;
            var y = slot.Top - inset.Top;
            var w = (slot.Right - slot.Left) + inset.Left + inset.Right;
            var h = (slot.Bottom - slot.Top) + inset.Top + inset.Bottom;
            if (w <= 0 || h <= 0) continue;

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        return new ArrangeResult(area, count);
    }

    /// <summary>
    /// Difference between the Win32 window rectangle and the visible DWM
    /// frame, expressed as (left, top, right, bottom) insets. Zero for
    /// borderless/custom-drawn windows where DWM reports no difference.
    /// </summary>
    private static NativeMethods.RECT GetFrameInsets(IntPtr hwnd, NativeMethods.RECT outer)
    {
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var visible, Marshal.SizeOf<NativeMethods.RECT>()) != 0)
            return default;

        // Sanity guard: some Electron/UWP windows report wildly different
        // bounds (multi-monitor DPI transitions). Ignore anything absurd.
        var l = visible.Left - outer.Left;
        var t = visible.Top - outer.Top;
        var r = outer.Right - visible.Right;
        var b = outer.Bottom - visible.Bottom;
        if (l < 0 || t < 0 || r < 0 || b < 0 || l > 40 || t > 40 || r > 40 || b > 40)
            return default;

        return new NativeMethods.RECT { Left = l, Top = t, Right = r, Bottom = b };
    }

    /// <summary>
    /// Keeps windows in their relative position: with two panes the left
    /// window stays left (the case the user cares about most); with a grid the
    /// order follows reading order, row by row.
    /// </summary>
    private static void SortForReadingOrder(List<LayoutWindow> windows, bool landscape)
    {
        if (windows.Count <= 2)
        {
            windows.Sort((a, b) => landscape
                ? a.CenterX.CompareTo(b.CenterX)
                : a.CenterY.CompareTo(b.CenterY));
            return;
        }

        // On a portrait panel the major axis is Y, so group by column first.
        windows.Sort((a, b) =>
        {
            var major = landscape ? a.CenterY.CompareTo(b.CenterY) : a.CenterX.CompareTo(b.CenterX);
            if (major != 0) return major;
            return landscape ? a.CenterX.CompareTo(b.CenterX) : a.CenterY.CompareTo(b.CenterY);
        });
    }

    private static List<NativeMethods.RECT> BuildSlots(NativeMethods.RECT area, int n, bool landscape)
    {
        if (n == 1) return new List<NativeMethods.RECT> { area };

        if (n == 2)
            return Split(area, 2, alongX: landscape);

        if (n == 3)
        {
            // Windows Snap's 3-pane layout: one large half + two stacked
            // quarters on the other side (rotated for a portrait panel).
            var halves = Split(area, 2, alongX: landscape);
            var quarters = Split(halves[1], 2, alongX: !landscape);
            return new List<NativeMethods.RECT> { halves[0], quarters[0], quarters[1] };
        }

        // Four or more: an even grid, with the last row evenly filled by
        // whatever is left so no empty cells are created.
        var cols = (int)Math.Ceiling(Math.Sqrt(n));
        var rows = (int)Math.Ceiling((double)n / cols);
        if (!landscape) (rows, cols) = (cols, rows);

        var slots = new List<NativeMethods.RECT>(n);
        var remaining = n;
        for (var row = 0; row < rows; row++)
        {
            var rowRect = Slice(area, rows, row, alongX: false);
            var inRow = (int)Math.Ceiling((double)remaining / (rows - row));
            slots.AddRange(Split(rowRect, inRow, alongX: true));
            remaining -= inRow;
        }
        return slots;
    }

    private static List<NativeMethods.RECT> Split(NativeMethods.RECT area, int count, bool alongX)
    {
        var result = new List<NativeMethods.RECT>(count);
        for (var i = 0; i < count; i++)
            result.Add(Slice(area, count, i, alongX));
        return result;
    }

    /// <summary>
    /// Integer-rounded slice so neighbouring panes share an exact edge — no
    /// 1 px gap or overlap from truncating division.
    /// </summary>
    private static NativeMethods.RECT Slice(NativeMethods.RECT area, int count, int index, bool alongX)
    {
        if (alongX)
        {
            var start = area.Left;
            var total = area.Right - area.Left;
            return new NativeMethods.RECT
            {
                Left = start + (int)Math.Round((double)total * index / count),
                Top = area.Top,
                Right = start + (int)Math.Round((double)total * (index + 1) / count),
                Bottom = area.Bottom,
            };
        }

        var top = area.Top;
        var height = area.Bottom - area.Top;
        return new NativeMethods.RECT
        {
            Left = area.Left,
            Top = top + (int)Math.Round((double)height * index / count),
            Right = area.Right,
            Bottom = top + (int)Math.Round((double)height * (index + 1) / count),
        };
    }

    private static NativeMethods.RECT Clamp(NativeMethods.RECT r, NativeMethods.RECT area) => new()
    {
        Left = Math.Max(area.Left, r.Left),
        Top = Math.Max(area.Top, r.Top),
        Right = Math.Min(area.Right, r.Right),
        Bottom = Math.Min(area.Bottom, r.Bottom),
    };

    private static bool SameRect(NativeMethods.RECT a, NativeMethods.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
}
