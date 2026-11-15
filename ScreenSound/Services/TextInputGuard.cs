using System.Runtime.InteropServices;
using System.Text;
using ScreenSound.Interop;

namespace ScreenSound.Services;

/// <summary>
/// Keeps global hotkeys from eating characters while the user is typing.
///
/// <see cref="NativeMethods.RegisterHotKey"/> is a system-wide grab: as long as
/// a bare F or U is registered, that key never reaches the focused application.
/// Filtering after the fact cannot help — the character is already gone — so
/// this guard releases the hotkeys *before* the first keystroke, driven by
/// EVENT_SYSTEM_FOREGROUND / EVENT_OBJECT_FOCUS notifications.
///
/// "Typing" is detected from the foreground GUI thread's
/// <see cref="NativeMethods.GUITHREADINFO"/>: a non-null <c>hwndCaret</c> means
/// the app created a text insertion caret, and a focused native edit-class
/// window covers the few controls that don't. This needs no
/// AttachThreadInput (which can deadlock across processes) and no global
/// keyboard hook (which security suites understandably dislike).
/// </summary>
public sealed class TextInputGuard : System.IDisposable
{
    /// <summary>
    /// Native edit controls that unambiguously mean "text is being entered".
    /// Deliberately conservative: browser/Chromium render widgets are NOT in
    /// this list, because those stay focused while merely reading a page —
    /// their text fields are caught by the caret check instead.
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> EditableClasses =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            "Edit", "RichEdit20A", "RichEdit20W", "RichEdit50W", "RICHEDIT60W",
            "RichEditD2DPT", "Scintilla", "ATL:Edit", "TMemo", "TEdit",
            "WindowsForms10.EDIT", "ThunderRT6TextBox", "msctls_hotkey32",
        };

    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    // WinEvent callbacks are invoked by USER32 on the installing thread. The
    // delegate must be rooted for the lifetime of the hook or the GC will
    // collect it and the next callback will crash the process.
    private readonly NativeMethods.WinEventProc _callback;
    private readonly System.IntPtr _foregroundHook;
    private readonly System.IntPtr _focusHook;

    private int _notifyPending;
    private bool _disposed;

    /// <summary>Raised on the UI thread whenever focus may have changed.</summary>
    public event System.Action? FocusMayHaveChanged;

    public TextInputGuard(System.Windows.Threading.Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _callback = OnWinEvent;

        // Two exact-range hooks rather than one wide range (0x0003..0x8005
        // would deliver every accessibility event on the system).
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            System.IntPtr.Zero, _callback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        _focusHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_FOCUS, NativeMethods.EVENT_OBJECT_FOCUS,
            System.IntPtr.Zero, _callback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
    }

    private void OnWinEvent(System.IntPtr hook, uint eventType, System.IntPtr hwnd,
                            int idObject, int idChild, uint eventThread, uint eventTime)
    {
        // EVENT_OBJECT_FOCUS is chatty (every control click, every caret move
        // in some apps). Coalesce a burst into one UI-thread notification.
        if (System.Threading.Interlocked.Exchange(ref _notifyPending, 1) != 0) return;

        _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new System.Action(() =>
            {
                System.Threading.Interlocked.Exchange(ref _notifyPending, 0);
                if (!_disposed) FocusMayHaveChanged?.Invoke();
            }));
    }

    /// <summary>True when the foreground app currently owns a text caret.</summary>
    public static bool IsTextInputActive()
    {
        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == System.IntPtr.Zero) return false;

            var threadId = NativeMethods.GetWindowThreadProcessId(foreground, out _);
            if (threadId == 0) return false;

            var info = new NativeMethods.GUITHREADINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
            };
            if (!NativeMethods.GetGUIThreadInfo(threadId, ref info)) return false;

            // Win32 Edit / RichEdit / WPF TextBox / Chromium input fields all
            // create a caret while they accept text.
            if (info.hwndCaret != System.IntPtr.Zero) return true;

            if (info.hwndFocus != System.IntPtr.Zero)
            {
                var sb = new StringBuilder(128);
                if (NativeMethods.GetClassName(info.hwndFocus, sb, sb.Capacity) > 0
                    && EditableClasses.Contains(sb.ToString()))
                    return true;
            }
            return false;
        }
        catch
        {
            // A foreground window can disappear mid-query. Failing open (i.e.
            // reporting "not typing") keeps the hotkey usable.
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_foregroundHook != System.IntPtr.Zero) NativeMethods.UnhookWinEvent(_foregroundHook);
        if (_focusHook != System.IntPtr.Zero) NativeMethods.UnhookWinEvent(_focusHook);
    }
}
