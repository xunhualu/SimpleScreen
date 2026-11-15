using ScreenSound.Interop;
using ScreenSound.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenSound.Services;

public class MonitorService
{
    private List<MonitorInfo>? _cachedMonitors;
    private DateTime _lastRefresh = DateTime.MinValue;
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromSeconds(5);

    public List<MonitorInfo> GetMonitors(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedMonitors != null && DateTime.UtcNow - _lastRefresh < CacheExpiry)
            return _cachedMonitors;

        var monitors = new List<MonitorInfo>();
        int index = 1;

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT lprcMonitor, IntPtr dwData) =>
        {
            var info = new NativeMethods.MONITORINFOEX();
            info.cbSize = Marshal.SizeOf(info);

            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                var deviceName = info.szDevice?.TrimEnd('\0') ?? $"DISPLAY{index}";
                var friendlyName = GetMonitorFriendlyName(deviceName) ?? $"Monitor {index}";
                monitors.Add(new MonitorInfo
                {
                    Handle = hMonitor,
                    DeviceName = deviceName,
                    FriendlyName = friendlyName,
                    IdentityKey = MonitorDetailsService.GetIdentityKey(deviceName),
                    Left = info.rcMonitor.Left,
                    Top = info.rcMonitor.Top,
                    Width = info.rcMonitor.Right - info.rcMonitor.Left,
                    Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                    IsPrimary = (info.dwFlags & 1) != 0
                });
                index++;
            }
            return true;
        }, IntPtr.Zero);

        _cachedMonitors = monitors;
        _lastRefresh = DateTime.UtcNow;
        return monitors;
    }

    private static string? GetMonitorFriendlyName(string deviceName)
    {
        try
        {
            var dd = new NativeMethods.DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(dd);

            // Query the monitor attached to this display adapter
            if (NativeMethods.EnumDisplayDevices(deviceName, 0, ref dd, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME))
            {
                var name = dd.DeviceString?.Trim();
                if (!string.IsNullOrEmpty(name) && name != "Generic PnP Monitor")
                    return name;
            }
        }
        catch { }

        return null;
    }

    public void InvalidateCache()
    {
        _cachedMonitors = null;
    }

    // The window we treat as "the app's main window", per owning process, plus
    // the monitor it was last seen on.
    //
    // Audio outlives the UI: players and call apps hide their window to the tray
    // (or minimise it) while playback keeps going, and the helper windows they
    // leave behind — desktop lyrics, mini player, floating toolbar — frequently
    // sit on a DIFFERENT screen. Re-picking "the biggest visible window" every
    // cycle would then mistake such a helper for the app and drag its audio onto
    // that screen. Remembering the window we already chose keeps hidden apps
    // anchored where the user left them.
    private sealed class WindowMemory
    {
        public IntPtr Hwnd;
        public string? DeviceName;
        public DateTime LastSeenUtc;
    }

    private readonly ConcurrentDictionary<uint, WindowMemory> _windowMemory = new();

    /// <summary>Drops remembered window state for a process that has exited.</summary>
    public void ForgetProcess(uint processId) => _windowMemory.TryRemove(processId, out _);

    public MonitorInfo? GetMonitorForProcess(uint processId)
    {
        // Try the exact PID first, then walk up the parent process tree.
        // Handles Electron apps (Discord, Slack, VS Code) where the audio
        // session runs in a child renderer process without a visible window.
        var pidsToTry = GetProcessAndAncestors(processId);

        var monitors = GetMonitors();
        if (monitors.Count == 0) return null;

        // ---- 1) Follow the window we already know -------------------------
        // Alive and on screen → follow it (the user may have dragged it).
        // Alive but hidden / minimised → stay on the screen it last occupied:
        // "I tucked the window away, my music belongs to that screen" — and
        // returning a monitor (rather than null) also lets the router pull the
        // audio back if something else moved the stream meanwhile.
        foreach (var pid in pidsToTry)
        {
            if (!_windowMemory.TryGetValue(pid, out var mem)) continue;

            if (!StillOwnedBy(mem.Hwnd, pid))
            {
                _windowMemory.TryRemove(pid, out _);
                continue;
            }

            if (!NativeMethods.IsIconic(mem.Hwnd) && NativeMethods.IsWindowVisible(mem.Hwnd)
                && !IsParkedOffDesktop(mem.Hwnd))
            {
                var live = FindMonitorByWindowPosition(mem.Hwnd, monitors)
                           ?? MonitorFromWindowFallback(mem.Hwnd, monitors);
                if (live != null)
                {
                    mem.DeviceName = live.DeviceName;
                    mem.LastSeenUtc = DateTime.UtcNow;
                    return live;
                }
            }

            var remembered = monitors.FirstOrDefault(m => m.DeviceName == mem.DeviceName);
            if (remembered != null)
            {
                mem.LastSeenUtc = DateTime.UtcNow;
                return remembered;
            }
        }

        // ---- 2) Nothing remembered yet — locate the real main window ------
        var hwnd = FindMainWindowForProcess(pidsToTry);
        if (hwnd == IntPtr.Zero) return null;

        // Zero-sized / parked outside the virtual desktop: report "unknown"
        // instead of letting the nearest-monitor maths snap the audio to
        // whichever screen happens to sit closest to the parking spot.
        if (IsParkedOffDesktop(hwnd)) return null;

        var resolved = FindMonitorByWindowPosition(hwnd, monitors)
                       ?? MonitorFromWindowFallback(hwnd, monitors);
        if (resolved == null) return null;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint ownerPid);
        _windowMemory[ownerPid] = new WindowMemory
        {
            Hwnd = hwnd,
            DeviceName = resolved.DeviceName,
            LastSeenUtc = DateTime.UtcNow
        };
        return resolved;
    }

    private static bool StillOwnedBy(IntPtr hwnd, uint pid)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint owner);
        return owner == pid;
    }

    private static bool IsParkedOffDesktop(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return true;
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return true;
        return rect.Right < -5000 || rect.Bottom < -5000;
    }

    private static MonitorInfo? MonitorFromWindowFallback(IntPtr hwnd, List<MonitorInfo> monitors)
    {
        var hMonitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero) return null;

        var match = monitors.FirstOrDefault(m => m.Handle == hMonitor);
        if (match != null) return match;

        var info = new NativeMethods.MONITORINFOEX();
        info.cbSize = Marshal.SizeOf(info);
        if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
        {
            var deviceName = info.szDevice?.TrimEnd('\0');
            match = monitors.FirstOrDefault(m => m.DeviceName == deviceName);
        }
        return match;
    }

    private MonitorInfo? FindMonitorByWindowPosition(IntPtr hwnd, List<MonitorInfo> monitors)
    {
        // Try DWM extended frame bounds first (gives physical/unscaled coordinates)
        int centerX, centerY;
        int hr = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
            out NativeMethods.RECT dwmRect, Marshal.SizeOf<NativeMethods.RECT>());

        if (hr == 0)
        {
            centerX = (dwmRect.Left + dwmRect.Right) / 2;
            centerY = (dwmRect.Top + dwmRect.Bottom) / 2;
        }
        else
        {
            // Fall back to GetWindowRect
            if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT windowRect))
                return null;
            centerX = (windowRect.Left + windowRect.Right) / 2;
            centerY = (windowRect.Top + windowRect.Bottom) / 2;
        }

        // Find which monitor contains the window center point
        foreach (var monitor in monitors)
        {
            if (centerX >= monitor.Left && centerX < monitor.Left + monitor.Width &&
                centerY >= monitor.Top && centerY < monitor.Top + monitor.Height)
            {
                return monitor;
            }
        }

        // If no exact match (window might be between monitors), find closest
        MonitorInfo? closest = null;
        double closestDist = double.MaxValue;
        foreach (var monitor in monitors)
        {
            double monCenterX = monitor.Left + monitor.Width / 2.0;
            double monCenterY = monitor.Top + monitor.Height / 2.0;
            double dist = Math.Pow(centerX - monCenterX, 2) + Math.Pow(centerY - monCenterY, 2);
            if (dist < closestDist)
            {
                closestDist = dist;
                closest = monitor;
            }
        }

        return closest;
    }

    private static List<uint> GetProcessAndAncestors(uint processId)
    {
        var pids = new List<uint> { processId };
        var seen = new HashSet<uint> { processId };
        var currentPid = processId;

        for (int i = 0; i < 5; i++)
        {
            try
            {
                var parentPid = GetParentProcessId(currentPid);
                if (parentPid == 0 || !seen.Add(parentPid))
                    break;
                pids.Add(parentPid);
                currentPid = parentPid;
            }
            catch
            {
                break;
            }
        }

        return pids;
    }

    private static uint GetParentProcessId(uint processId)
    {
        var handle = NativeMethods.CreateToolhelp32Snapshot(0x00000002, 0);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return 0;

        try
        {
            var entry = new NativeMethods.PROCESSENTRY32();
            entry.dwSize = (uint)Marshal.SizeOf(entry);

            if (!NativeMethods.Process32First(handle, ref entry))
                return 0;

            do
            {
                if (entry.th32ProcessID == processId)
                    return entry.th32ParentProcessID;
            } while (NativeMethods.Process32Next(handle, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }

        return 0;
    }

    /// <summary>
    /// Picks the window that represents an app, scanning the given processes
    /// (the audio session's own process plus its ancestors, so Chromium-style
    /// renderer → browser-parent chains resolve too).
    /// </summary>
    /// <remarks>
    /// Auxiliary windows are rejected on purpose. When a player hides its main
    /// window to the tray, what stays visible is a desktop-lyrics overlay, a
    /// mini player or a floating toolbar — none of which live where the app's
    /// audio belongs. Accepting one of those is precisely how audio used to end
    /// up on another screen's speaker.
    /// </remarks>
    private IntPtr FindMainWindowForProcess(List<uint> candidatePids)
    {
        var wanted = new HashSet<uint>(candidatePids);
        IntPtr bestWindow = IntPtr.Zero;
        long bestArea = 0;

        NativeMethods.EnumWindows((IntPtr hWnd, IntPtr lParam) =>
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (!wanted.Contains(pid)) return true;

            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            if (NativeMethods.IsIconic(hWnd)) return true;
            if (NativeMethods.GetWindowTextLength(hWnd) == 0) return true;

            long ex = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE).ToInt64();

            // Never a click-through / never-activating overlay (lyrics, HUDs).
            if ((ex & NativeMethods.WS_EX_NOACTIVATE) != 0) return true;

            // Tool palette / mini player: a tool window that is deliberately kept
            // off the taskbar is not the app's main UI.
            if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0 &&
                (ex & NativeMethods.WS_EX_APPWINDOW) == 0) return true;

            // Dialog owned by another window — follows its owner, not the app.
            if (NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;

            if (!NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT rect)) return true;
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w < 200 || h < 150) return true;

            long area = (long)w * h;
            if (area > bestArea)
            {
                bestArea = area;
                bestWindow = hWnd;
            }
            return true;
        }, IntPtr.Zero);

        return bestWindow;
    }
}
