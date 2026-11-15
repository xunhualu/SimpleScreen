using ScreenSound.Interop;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScreenSound.Services;

/// <summary>
/// Keeps a window's <em>physical</em> (pixel) size stable across monitors.
///
/// Windows — and therefore WPF — preserve the logical (DIP) size of a window
/// when it crosses a monitor boundary: a 900 DIP-wide window is 1350 px on a
/// 150 % panel and 1800 px on a 200 % one. On a mixed-DPI desk that reads as
/// "the window doubles in size when I drag it over", which is exactly what this
/// helper undoes: given the pixel size the window had on the old monitor, it
/// converts that back into DIPs for the new DPI, so the on-screen footprint
/// stays the same.
///
/// Everything here is best-effort and must never throw into the UI pump — a
/// window that ends up a few pixels off is fine, a crashed message loop is not.
/// </summary>
public static class WindowScaleService
{
    /// <summary>Physical size of <paramref name="window"/> in pixels, right now.</summary>
    public static (double Width, double Height) GetPixelSize(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        return (window.ActualWidth * dpi.DpiScaleX, window.ActualHeight * dpi.DpiScaleY);
    }

    // ── Real (ruler-accurate) pixel density ─────────────────────────────────
    //
    // "Same size on every monitor" means the same number of centimetres, and
    // pixels-per-centimetre is a property of the panel, not of the Windows DPI
    // scale. The scale factors are coarse presets (150 %, 200 %…) and are also
    // what we're compensating for in the first place, so they can't be used to
    // compare physical density. The panel's EDID block reports its true viewable
    // size — except that some panels (portable monitors in particular) ship a
    // template EDID that lies by 10–20 %, which is exactly what the
    // tape-measured <paramref name="widthCmOverrides"/> entries correct.

    /// <summary>
    /// Real pixel density of a monitor in pixels per inch. Keyed off the
    /// monitor's own device ID: native width from MONITORINFOEX ÷ physical
    /// width, where the physical width comes from the user's tape-measured
    /// override for that monitor model (if any) and falls back to EDID.
    /// Returns 0 when nothing is known — callers then keep the previous
    /// behaviour instead of guessing.
    /// </summary>
    public static double GetRealPpi(IntPtr hMonitor, IReadOnlyDictionary<string, double>? widthCmOverrides)
    {
        try
        {
            var info = new NativeMethods.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
            };
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref info)) return 0;

            var nativeWidth = info.rcMonitor.Right - info.rcMonitor.Left;
            if (nativeWidth <= 0) return 0;

            var widthCm = TryGetMeasuredWidthCm(info.szDevice, widthCmOverrides)
                          ?? GetEdidWidthCm(info.szDevice);
            if (widthCm <= 0) return 0;

            return nativeWidth / (widthCm / 2.54);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetRealPpi failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// The one monitor whose effective DPI matches <paramref name="dpiX"/>.
    /// Several entries can legitimately match: a panel hooked up through two
    /// cables shows up as two DISPLAYn devices with identical DPI — same
    /// panel, same density, so that's fine. Only genuinely *different*
    /// monitors sharing a scale factor are ambiguous, and those get
    /// <see cref="IntPtr.Zero"/> so the caller falls back instead of guessing.
    /// </summary>
    public static IntPtr FindMonitorByDpi(uint dpiX)
    {
        try
        {
            var matches = new List<(IntPtr Handle, string? Model)>();
            bool Callback(IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data)
            {
                if (NativeMethods.GetDpiForMonitor(hMonitor, 0 /* MDT_EFFECTIVE_DPI */, out var x, out _) == 0)
                {
                    if (Math.Abs(x - dpiX) <= 1)
                        matches.Add((hMonitor, GetMonitorModelForHandle(hMonitor)));
                }
                return true;
            }
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

            if (matches.Count == 0) return IntPtr.Zero;

            // Multiple matches are OK only when they're all the same panel.
            var firstModel = matches[0].Model;
            foreach (var (handle, model) in matches)
                if (model is null || firstModel is null || !string.Equals(model, firstModel, StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;

            return matches[0].Handle;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FindMonitorByDpi failed: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    private static string? GetMonitorModelForHandle(IntPtr hMonitor)
    {
        try
        {
            var info = new NativeMethods.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
            };
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref info)) return null;
            return GetMonitorModel(info.szDevice);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Monitor's current effective DPI (the 150 %, 200 % … value).</summary>
    public static bool TryGetEffectiveDpi(IntPtr hMonitor, out uint dpiX, out uint dpiY)
    {
        dpiX = 0;
        dpiY = 0;
        try
        {
            return NativeMethods.GetDpiForMonitor(hMonitor, 0, out dpiX, out dpiY) == 0 && dpiX > 0;
        }
        catch
        {
            return false;
        }
    }

    private static double? TryGetMeasuredWidthCm(string deviceName, IReadOnlyDictionary<string, double>? overrides)
    {
        if (overrides == null || overrides.Count == 0) return null;
        var model = GetMonitorModel(deviceName);
        if (model is null) return null;
        return overrides.TryGetValue(model, out var cm) && cm > 0 ? cm : null;
    }

    /// <summary>
    /// Viewable width in centimetres as reported by the monitor's EDID block,
    /// read from the same registry blob Windows uses. Byte 21 of an EDID 1.x
    /// holds the maximum horizontal image size; 0 means "unspecified".
    ///
    /// Note the path: EnumDisplayDevices hands out a pseudo DeviceID
    /// (MONITOR\&lt;model&gt;\{class-guid}\NNNN) that doesn't exist in the
    /// registry — the real hardware instances live under
    /// <c>Enum\DISPLAY\&lt;model&gt;</c>, so after extracting the model we
    /// enumerate that branch and take the first instance with a valid EDID.
    /// </summary>
    // Some portable panels ship a template EDID reused from a larger
    // desktop model. HIV0160 (2560x1600, 16:10) reports 34 x 22 cm but
    // measures 30.2 x 18.8 cm with a ruler (~11 % off), which makes its
    // computed PPI identical to a 34 cm 2560x1440 panel and silently
    // disables the density correction. Windows cannot detect this, so
    // keep tape-measured corrections for known liars here. User entries
    // in AppSettings.MonitorWidthCm still take precedence (see GetRealPpi).
    private static readonly Dictionary<string, double> KnownEdidWidthCorrections =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["HIV0160"] = 30.2,
    };

    private static double GetEdidWidthCm(string deviceName)
    {
        try
        {
            var model = GetMonitorModel(deviceName);
            if (model is null) return 0;

            using var branch = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{model}");
            if (branch is null) return 0;

            foreach (var instanceName in branch.GetSubKeyNames())
            {
                using var deviceParams = branch.OpenSubKey($@"{instanceName}\Device Parameters");
                if (deviceParams?.GetValue("EDID") is not byte[] edid || edid.Length < 23) continue;

                var cm = edid[21];
                if (cm > 0)
                    return KnownEdidWidthCorrections.TryGetValue(model, out var measured)
                        ? measured : cm;
            }

            return 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetEdidWidthCm failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Stable identity of the panel itself, e.g. "HIV0160" — shared by every
    /// cable/connection the panel is plugged in through (the DISPLAYn number
    /// changes, the model doesn't).
    /// </summary>
    private static string? GetMonitorModel(string deviceName)
    {
        try
        {
            var dd = new NativeMethods.DISPLAY_DEVICE
            {
                cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>()
            };
            if (!NativeMethods.EnumDisplayDevices(deviceName, 0, ref dd, 0)) return null;
            if (string.IsNullOrEmpty(dd.DeviceID)) return null;

            // MONITOR\<model>\{...} → <model>
            var parts = dd.DeviceID.Split('\\');
            return parts.Length >= 2 ? parts[1] : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resizes <paramref name="window"/> so it occupies roughly
    /// <paramref name="pixelWidth"/> × <paramref name="pixelHeight"/> physical
    /// pixels on the monitor it currently sits on, then nudges it back inside
    /// that monitor's work area.
    /// </summary>
    /// <remarks>
    /// The requested size is clamped twice, deliberately:
    /// * to the window's <see cref="FrameworkElement.MinWidth"/>/<c>MinHeight</c>
    ///   (WPF would coerce anyway, but doing it here keeps the "did it work?"
    ///   answer honest), and
    /// * to the target monitor's work area — a window bigger than the screen it
    ///   lives on is unusable, and keeping it "the same size" is not worth
    ///   losing the title bar behind the taskbar for.
    /// </remarks>
    public static bool ApplyPixelSize(Window window, double pixelWidth, double pixelHeight)
    {
        if (double.IsNaN(pixelWidth) || double.IsNaN(pixelHeight) ||
            pixelWidth < 1 || pixelHeight < 1)
            return false;

        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);

            // px → DIP on the *new* monitor: DIP = px / (dpi / 96).
            var targetWidth = pixelWidth / dpi.DpiScaleX;
            var targetHeight = pixelHeight / dpi.DpiScaleY;

            if (TryGetWorkAreaDip(window, dpi, out var workWidth, out var workHeight))
            {
                targetWidth = Math.Min(targetWidth, workWidth);
                targetHeight = Math.Min(targetHeight, workHeight);
            }

            targetWidth = Math.Max(window.MinWidth, targetWidth);
            targetHeight = Math.Max(window.MinHeight, targetHeight);

            // Sub-pixel differences aren't worth a resize (and would let
            // rounding noise fight the user's own drag).
            var needsResize = Math.Abs(window.Width - targetWidth) > 0.5 ||
                              Math.Abs(window.Height - targetHeight) > 0.5;

            if (needsResize)
            {
                // Update the WPF model first, then push the same size through
                // Win32. The logical assignment alone is unreliable here: right
                // after a monitor switch WPF is still finishing its own DPI
                // transition, and it overwrites whatever Width/Height we set
                // with the DPI-preserving rectangle it computed. A direct
                // SetWindowPos after that transition sticks — and WPF picks the
                // new size up from WM_SIZE, so both views agree afterwards.
                window.Width = targetWidth;
                window.Height = targetHeight;

                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.SetWindowPos(
                        hwnd, IntPtr.Zero, 0, 0,
                        (int)Math.Round(targetWidth * dpi.DpiScaleX),
                        (int)Math.Round(targetHeight * dpi.DpiScaleY),
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                }
            }

            ClampIntoWorkArea(window);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WindowScaleService.ApplyPixelSize failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Moves the window just enough to sit fully inside the work area of the
    /// monitor it's on. Only used after a size change: a window that grew on a
    /// smaller screen can end up with its edges (or its title bar) off-screen.
    /// </summary>
    public static void ClampIntoWorkArea(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
            };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;

            var dx = 0;
            var dy = 0;

            if (rect.Right > info.rcWork.Right) dx = info.rcWork.Right - rect.Right;
            if (rect.Left + dx < info.rcWork.Left) dx = info.rcWork.Left - rect.Left;
            if (rect.Bottom > info.rcWork.Bottom) dy = info.rcWork.Bottom - rect.Bottom;
            if (rect.Top + dy < info.rcWork.Top) dy = info.rcWork.Top - rect.Top;

            if (dx == 0 && dy == 0) return;

            NativeMethods.SetWindowPos(
                hwnd, IntPtr.Zero,
                rect.Left + dx, rect.Top + dy, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WindowScaleService.ClampIntoWorkArea failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes sure the window still has a grabbable title bar on one of the
    /// monitors Windows currently knows about, and parks it on the primary
    /// monitor when it doesn't.
    ///
    /// Windows happily leaves a window at coordinates that no longer belong to
    /// any display — unplug the second monitor, or close the lid of the one the
    /// app was last used on, and the next launch restores a window nobody can
    /// see or reach. Because this app also refuses to start twice, the only
    /// symptom is "already running" and no window; there is no way back short of
    /// deleting settings.json. This is the way back.
    /// </summary>
    /// <returns>True when the window had to be moved.</returns>
    public static bool EnsureOnScreen(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            // A minimised window is parked at -32000,-32000 by design.
            if (NativeMethods.IsIconic(hwnd)) return false;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;

            var areas = GetMonitorWorkAreas();
            if (areas.Count == 0) return false;

            // "Reachable" = part of the title bar is on some monitor. A window
            // whose body peeks out but whose title bar is gone can be neither
            // dragged nor double-clicked, which is the case worth fixing.
            var barHeight = Math.Clamp((rect.Bottom - rect.Top) / 6, 8, 40);
            var bar = new NativeMethods.RECT
            {
                Left = rect.Left + 24,
                Top = rect.Top,
                Right = rect.Right - 24,
                Bottom = rect.Top + barHeight
            };
            if (bar.Right <= bar.Left) bar.Right = bar.Left + 1;

            foreach (var area in areas)
                if (Intersects(bar, area.Rect)) return false;

            var target = areas[0];
            foreach (var area in areas)
            {
                if (IsPrimary(area)) { target = area; break; }
            }

            var r = target.Rect;
            var width = Math.Min(rect.Right - rect.Left, r.Right - r.Left);
            var height = Math.Min(rect.Bottom - rect.Top, r.Bottom - r.Top);
            var x = r.Left + (r.Right - r.Left - width) / 2;
            var y = r.Top + (r.Bottom - r.Top - height) / 2;

            NativeMethods.SetWindowPos(
                hwnd, IntPtr.Zero, x, y, width, height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

            // Keep the WPF model in step with what Win32 now shows, otherwise
            // the next layout pass would push its own idea of the size back.
            var dpi = VisualTreeHelper.GetDpi(window);
            if (dpi.DpiScaleX > 0 && dpi.DpiScaleY > 0)
            {
                window.Width = width / dpi.DpiScaleX;
                window.Height = height / dpi.DpiScaleY;
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WindowScaleService.EnsureOnScreen failed: {ex.Message}");
            return false;
        }
    }

    private readonly record struct WorkArea(NativeMethods.RECT Rect, bool Primary);

    private static List<WorkArea> GetMonitorWorkAreas()
    {
        var result = new List<WorkArea>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data)
        {
            var info = new NativeMethods.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
            };

            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                // MONITORINFOF_PRIMARY
                result.Add(new WorkArea(info.rcWork, (info.dwFlags & 1) != 0));
            }
            else
            {
                result.Add(new WorkArea(rect, false));
            }

            return true;
        }

        try
        {
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EnumDisplayMonitors failed: {ex.Message}");
        }

        return result;
    }

    private static bool IsPrimary(WorkArea area) => area.Primary;

    private static bool Intersects(NativeMethods.RECT a, NativeMethods.RECT b)
        => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    /// <summary>Work area of the window's current monitor, converted to DIPs for its DPI.</summary>
    private static bool TryGetWorkAreaDip(Window window, DpiScale dpi, out double width, out double height)
    {
        width = 0;
        height = 0;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return false;

        width = (info.rcWork.Right - info.rcWork.Left) / dpi.DpiScaleX;
        height = (info.rcWork.Bottom - info.rcWork.Top) / dpi.DpiScaleY;
        return width > 0 && height > 0;
    }
}
