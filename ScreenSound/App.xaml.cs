using ScreenSound.Interop;
using ScreenSound.Services;
using ScreenSound.Views;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Data;

namespace ScreenSound;

public partial class App : Application
{
    // Single-instance guard. Two ScreenSound processes would race on the
    // same HKCU Run-at-startup state, both create tray icons, and both
    // fight over the per-session audio-policy calls for the same sessions.
    // The "Local\" prefix scopes the mutex to the current Windows login
    // session, so fast-user-switching still lets each user have their own
    // copy while a double-click on the shortcut won't spawn a second one.
    private const string SingleInstanceMutexName = @"Local\SimpleScreen-SingleInstance";
    private static Mutex? _singleInstanceMutex;

    // Second-instance hand-off. When the mutex is already held, the newcomer
    // pokes this event instead of showing a dead-end "already running" dialog —
    // the running copy surfaces its window, which matters a lot when that window
    // is hidden in the tray or parked on a monitor that has since been unplugged.
    private const string ActivateEventName = @"Local\SimpleScreen-Activate";
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activationWait;

    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Apply the UI language before ANY window or dialog is constructed, so
        // the app never paints a frame in the wrong language. Passing the raw
        // saved value lets LocalizationService fall back to the OS UI language
        // when the settings file predates this feature (empty string).
        var savedLanguage = new SettingsService().Load().Language;
        LocalizationService.Apply(savedLanguage);

        // Acquire the mutex FIRST, before any side effects (DPI config,
        // window creation, tray icon). If another copy is already running
        // we want to bail out cleanly with zero partial initialisation.
        _singleInstanceMutex = new Mutex(initiallyOwned: false, name: SingleInstanceMutexName);
        bool acquired;
        try
        {
            acquired = _singleInstanceMutex.WaitOne(millisecondsTimeout: 0);
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed without releasing. WaitOne still
            // hands us ownership in that case, so treat as success — the
            // mutex is now ours.
            acquired = true;
        }

        if (!acquired)
        {
            // Someone is already running: ask it to show itself, then leave.
            // Only fall back to the modal dialog when that hand-off fails —
            // otherwise a minimised-to-tray instance looks unreachable.
            if (TrySignalRunningInstance())
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                Shutdown();
                return;
            }

            MessageBox.Show(
                LocalizationService.Get("App_AlreadyRunning"),
                "SimpleScreen",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        // Create the hand-off event as early as possible so a second launch
        // that races us still finds it.
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activationWait = ThreadPool.RegisterWaitForSingleObject(
                _activateEvent, OnActivateSignalled, null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Activation event unavailable: {ex.Message}");
        }

        // Force PerMonitorV2 DPI awareness before WPF creates any windows.
        try
        {
            NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { /* Already set via manifest, or older Windows */ }

        base.OnStartup(e);

        // Create window manually so we can control visibility
        var window = new MainWindow();
        _window = window;
        MainWindow = window;

        if (!window.ShouldStartMinimized)
        {
            window.Show();
        }
        // Otherwise, tray icon is already set up — window stays hidden
    }

    /// <summary>
    /// Asks the already-running copy to show its window. Returns false when the
    /// event isn't there (older build, or the other instance is still starting).
    /// </summary>
    private static bool TrySignalRunningInstance()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
                {
                    using (handle)
                    {
                        handle.Set();
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Activation signal failed: {ex.Message}");
                return false;
            }

            // The other instance creates the event a hair after taking the
            // mutex; give it a moment rather than skipping straight to the
            // dialog.
            Thread.Sleep(80);
        }

        return false;
    }

    /// <summary>
    /// Runs on a pool thread when a second launch asks us to come forward.
    /// </summary>
    private void OnActivateSignalled(object? state, bool timedOut)
    {
        if (timedOut) return;

        try
        {
            Dispatcher.BeginInvoke(new Action(() => _window?.ShowFromTray()));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Activation dispatch failed: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Release + dispose so an immediate relaunch can acquire cleanly.
        // Without this, the OS would still reap the mutex on process exit,
        // but explicit release avoids a brief race window where a fast
        // double-click sees the mutex as still held.
        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { /* Not owned (e.g. after an AbandonedMutexException path) — nothing to release. */ }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        if (_activateEvent != null)
        {
            _activationWait?.Unregister(null);
            _activationWait = null;
            _activateEvent.Dispose();
            _activateEvent = null;
        }
        base.OnExit(e);
    }
}

public class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int count)
            return count == 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// <c>null</c> → Visible, non-null → Collapsed. Drives fallback visuals
/// (e.g. a pin glyph in place of an app icon that hasn't been captured yet
/// because the pinned app isn't currently playing audio).
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value == null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Non-null → Visible, <c>null</c> → Collapsed. Pairs with
/// <see cref="NullToVisibilityConverter"/> for the primary/fallback pattern.
/// </summary>
public class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Non-empty/whitespace string → Visible, empty/null → Collapsed. Used by the
/// About page to show the "Open release page" hyperlink only when the update
/// check has produced a URL to link to.
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => !string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Flips a bool. Lets us bind an <c>IsEnabled</c> to a "busy" flag without
/// duplicating the inverse state in the ViewModel — e.g. "button is enabled
/// when <c>IsCheckingForUpdate</c> is false".
/// </summary>
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
