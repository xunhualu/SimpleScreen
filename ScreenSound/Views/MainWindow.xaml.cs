using ScreenSound.Interop;
using ScreenSound.Services;
using ScreenSound.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace ScreenSound.Views;

public partial class MainWindow : UiWindow
{
    private readonly MainWindowViewModel _viewModel;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayShowItem;
    private Forms.ToolStripMenuItem? _trayExitItem;
    private bool _forceClose;

    private Border? _activePage;
    private readonly Border[] _pages;

    // One-shot correction applied 250 ms after a monitor switch (see
    // OnDpiChanged). A single timer rather than one per event: dragging quickly
    // across the boundary fires several OnDpiChanged calls, and per-event timers
    // would each replay their own (now stale) pixel size, last-one-wins.
    private readonly DispatcherTimer _scaleTimer;
    private double _pendingPixelWidth;
    private double _pendingPixelHeight;

    // Design-time minimum sizes from XAML (720 × 560 DIP). They are a floor in
    // *physical* terms: 720 DIP at 150 % is a much larger window than 720 DIP
    // at 200 %, so the DPI-sync path rescales MinWidth/MinHeight as the window
    // crosses monitors instead of letting a fixed DIP floor clamp the restored
    // footprint back up (that clamp is what made the window visibly grow).
    private readonly double _designMinWidth;
    private readonly double _designMinHeight;

    // ── Global "jump window" hotkey ──────────────────────────────────────────
    //
    // RegisterHotKey ties the combo to this window's HWND. The HWND survives
    // MinimizeOnClose (Hide keeps the HWND alive), so the hotkey keeps working
    // while the app lives in the tray — the intended usage: focus some window,
    // press the key, it lands on the other screen.

    private const int JumpHotkeyId = 0x5353;    // arbitrary, unique within this HWND
    private const int ArrangeHotkeyId = 0x5354; // second global hotkey: tile windows
    private IntPtr _hotkeyHwnd;
    private bool _jumpRegistered;
    private bool _arrangeRegistered;

    /// <summary>0 = idle, 1 = editing the jump hotkey, 2 = editing the arrange hotkey.</summary>
    private int _recordingTarget;

    /// <summary>Which cell of the armed hotkey is listening: 0 = modifier, 1 = main key.</summary>
    private int _recordingSlot;

    /// <summary>MOD_* bits seen so far while the modifier cell is armed.</summary>
    private int _pendingModifiers;

    /// <summary>
    /// Cached "the foreground app owns a text caret" state. Only bare-key
    /// hotkeys yield to it; modifier combos never collide with typing, so they
    /// stay registered and always work.
    /// </summary>
    private bool _typingActive;

    /// <summary>
    /// Watches foreground/focus changes so a bare F/U hotkey can step aside
    /// the instant a text caret appears. Uses WinEvent hooks (an accessibility
    /// API), not a global keyboard hook — no keylogger-shaped behaviour and no
    /// security-software false positives.
    /// </summary>
    private readonly TextInputGuard _textInputGuard;

    /// <summary>Safety net in case a focus event is missed.</summary>
    private readonly DispatcherTimer _hotkeyGuardTimer;

    /// <summary>
    /// Drives the "Auto-optimise layout" switch. Ticked on a dispatcher timer
    /// so the whole check runs on the UI thread (EnumWindows callbacks are
    /// marshalled through a delegate we own).
    /// </summary>
    private readonly AutoArrangeWatcher _autoArrangeWatcher = new();
    private readonly DispatcherTimer _autoArrangeTimer;

    // Absolute safety floor for the rescaled minimums — keeps the window
    // grabbable and its content legible even if a corrupted settings file
    // asks for an absurdly tiny restore size.
    private const double MinWidthFloor = 240;
    private const double MinHeightFloor = 160;

    public bool ShouldStartMinimized => _viewModel.StartMinimized;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;

        _designMinWidth = MinWidth;
        _designMinHeight = MinHeight;

        // Order must match the Tag indices on the sidebar RadioButtons:
        //   0 = Home, 1 = Pinned, 2 = Settings, 3 = About, 4 = Hotkeys.
        _pages = new[] { HomePage, PinnedPage, SettingsPage, AboutPage, HotkeyPage, MonitorDetailsPage };

        _scaleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _scaleTimer.Tick += (_, _) =>
        {
            _scaleTimer.Stop();
            SyncWindowScale(_pendingPixelWidth, _pendingPixelHeight);
        };

        // The watcher itself decides whether to act (Enabled + baseline rules);
        // keeping the timer always running makes switching the toggle instant.
        _autoArrangeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _autoArrangeTimer.Tick += (_, _) => _autoArrangeWatcher.Tick();
        _autoArrangeTimer.Start();

        _textInputGuard = new TextInputGuard(Dispatcher);
        _textInputGuard.FocusMayHaveChanged += ReevaluateHotkeys;
        _hotkeyGuardTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _hotkeyGuardTimer.Tick += (_, _) => ReevaluateHotkeys();
        _hotkeyGuardTimer.Start();

        SetupTrayIcon();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Listen for system theme changes to re-override when in manual mode
        Theme.Changed += OnGlobalThemeChanged;

        // Tray menu lives outside WPF's resource system — retitle it by hand
        LocalizationService.LanguageChanged += OnLanguageChanged;

        Loaded += (_, _) =>
        {
            // Start watcher for Mica backdrop effect (always needed)
            Watcher.Watch(this, BackgroundType.Mica, true);

            // Override with the user's chosen theme
            ApplyTheme(_viewModel.SelectedThemeIndex);

            ShowPage(0, animate: false);

            // A window restored from a session where the monitor layout was
            // different can end up on coordinates no display owns (second
            // screen unplugged / lid closed). Pull it back before anything
            // else, so the size restore below measures the real monitor.
            if (WindowScaleService.EnsureOnScreen(this))
                Activate();

            // Restore the pixel size recorded at the end of the last session.
            // Done here, not in the ctor: converting px → DIP needs the window's
            // HWND, because only then does WPF know which monitor (and therefore
            // which DPI) the window actually landed on.
            if (_viewModel.IsWindowScaleSyncEnabled)
            {
                var (pixelWidth, pixelHeight) = _viewModel.RememberedWindowPixelSize;
                if (pixelWidth > 0 && pixelHeight > 0)
                    RestorePixelSize(pixelWidth, pixelHeight);
            }

            // Wire the global hotkeys: WM_HOTKEY is delivered to the HWND,
            // which only exists now. Registration itself re-runs on every
            // settings change (see OnViewModelPropertyChanged).
            _hotkeyHwnd = new WindowInteropHelper(this).Handle;
            if (HwndSource.FromHwnd(_hotkeyHwnd) is { } source)
                source.AddHook(HotkeyWndProc);
            ApplyHotkeys();

            // LoadSettings ran in the view-model constructor, before this
            // window subscribed to PropertyChanged, so the persisted
            // auto-arrange toggle has to be applied explicitly once on startup.
            _autoArrangeWatcher.SetEnabled(_viewModel.IsAutoArrangeEnabled);
        };
    }

    /// <summary>
    /// Fires when the window lands on a monitor with a different DPI scale —
    /// i.e. exactly the moment Windows would change the window's physical size
    /// (a 900 DIP window is 1350 px wide at 150 % and 1800 px at 200 %).
    ///
    /// With "window size sync" on we take the pixel size the window had on the
    /// monitor it just left and convert it back to DIPs for the new DPI, so the
    /// on-screen footprint is unchanged. WPF overwrites the window rectangle with
    /// its own suggested one while it finishes the DPI transition, which is why
    /// the correction is applied twice — once now, once a beat later.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        if (!_viewModel.IsWindowScaleSyncEnabled) return;
        if (WindowState != WindowState.Normal) return;

        // WPF keeps the logical size across the change, so multiplying by the
        // OLD scale recovers the physical size the window had a moment ago.
        var pixelWidth = ActualWidth * oldDpi.DpiScaleX;
        var pixelHeight = ActualHeight * oldDpi.DpiScaleY;

        if (pixelWidth < 1 || pixelHeight < 1) return;

        // "Same size" means same number of centimetres, and pixels-per-cm is a
        // property of the panel, not of the DPI scale. Convert the footprint
        // through both monitors' real pixel density (EDID, corrected by the
        // user's tape-measured overrides when a panel lies about its size).
        // Falls back to plain pixel-preservation when either density is
        // unknown or the left monitor can't be identified.
        try
        {
            // Identify the two monitors by their *DPI*, never by where the
            // window happens to be right now. During a real drag the DPI
            // change fires while the window still straddles the boundary,
            // so MonitorFromWindow returns the OLD monitor and the
            // correction would be skipped (dragged windows kept their pixel
            // size instead of their centimetre size — exactly the "works in
            // tests, wrong when I drag it myself" mismatch). Scales are
            // unique per monitor here, so FindMonitorByDpi is reliable and
            // position-independent; genuinely ambiguous desks (two different
            // panels at the same scale) still fall back to pixel-preservation.
            var oldMonitor = WindowScaleService.FindMonitorByDpi((uint)Math.Round(oldDpi.DpiScaleX * 96));
            var newMonitor = WindowScaleService.FindMonitorByDpi((uint)Math.Round(newDpi.DpiScaleX * 96));
            if (newMonitor != IntPtr.Zero && oldMonitor != IntPtr.Zero && newMonitor != oldMonitor)
            {
                var oldPpi = WindowScaleService.GetRealPpi(oldMonitor, _viewModel.MonitorWidthCmOverride, _viewModel.MonitorDiagonalInchesOverride);
                var newPpi = WindowScaleService.GetRealPpi(newMonitor, _viewModel.MonitorWidthCmOverride, _viewModel.MonitorDiagonalInchesOverride);
                if (oldPpi > 0 && newPpi > 0 && Math.Abs(oldPpi - newPpi) > 0.5)
                {
                    var densityRatio = newPpi / oldPpi;
                    pixelWidth *= densityRatio;
                    pixelHeight *= densityRatio;
                }
            }
        }
        catch { /* best-effort: keep the unconverted footprint */ }

        // Carry the minimum-size floor across in *physical* terms too. Without
        // this, a 1350 px window from the 150 % screen asks for 675 DIP on a
        // 200 % screen, gets clamped up to the XAML MinWidth of 720 DIP and
        // ends up 1440 px — the "it grows a little when I drag it over" bug.
        // Scaling the current floor by the same ratio as the window keeps
        // both consistent, and the multiplication is idempotent across
        // round trips (720 → 540 → 720 for 150 % ↔ 200 %).
        var ratioX = oldDpi.DpiScaleX / newDpi.DpiScaleX;
        var ratioY = oldDpi.DpiScaleY / newDpi.DpiScaleY;
        if (Math.Abs(ratioX - 1) > 0.001)
            MinWidth = Math.Max(MinWidthFloor, Math.Min(_designMinWidth, MinWidth * ratioX));
        if (Math.Abs(ratioY - 1) > 0.001)
            MinHeight = Math.Max(MinHeightFloor, Math.Min(_designMinHeight, MinHeight * ratioY));

        // WPF applies its own suggested rectangle (which preserves the *logical*
        // size — i.e. exactly what we're trying to avoid) while it finishes the
        // DPI transition. So: correct it once immediately, and once more shortly
        // after, because the second pass is the one that lands after WPF has
        // written its rectangle and therefore sticks.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            SyncWindowScale(pixelWidth, pixelHeight)));

        // Re-arm the shared timer with the *newest* pair. A second switch within
        // the 250 ms window replaces the pending value instead of adding another
        // timer that would replay the old size a moment later.
        _pendingPixelWidth = pixelWidth;
        _pendingPixelHeight = pixelHeight;
        _scaleTimer.Stop();
        _scaleTimer.Start();
    }

    /// <summary>
    /// Applies a remembered pixel footprint at startup / tray-restore. The
    /// footprint was legal on the monitor it was saved on; if it maps to fewer
    /// DIPs here than the XAML minimum (typical when the last session ran on a
    /// high-DPI screen), lower the floor for this session instead of letting
    /// it distort the restore — same reasoning as the rescaling in
    /// <see cref="OnDpiChanged"/>.
    /// </summary>
    private void RestorePixelSize(double pixelWidth, double pixelHeight)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        MinWidth = Math.Max(MinWidthFloor,
            Math.Min(MinWidth, pixelWidth / dpi.DpiScaleX));
        MinHeight = Math.Max(MinHeightFloor,
            Math.Min(MinHeight, pixelHeight / dpi.DpiScaleY));

        WindowScaleService.ApplyPixelSize(this, pixelWidth, pixelHeight);
    }

    /// <summary>
    /// Applies the pixel size captured before a monitor switch, unless the user
    /// has toggled the option off or maximised the window in the meantime.
    /// </summary>
    private void SyncWindowScale(double pixelWidth, double pixelHeight)
    {
        if (!_viewModel.IsWindowScaleSyncEnabled) return;
        if (WindowState != WindowState.Normal) return;

        WindowScaleService.ApplyPixelSize(this, pixelWidth, pixelHeight);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedThemeIndex))
        {
            ApplyTheme(_viewModel.SelectedThemeIndex);
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.IsJumpHotkeyEnabled)
                 or nameof(MainWindowViewModel.JumpHotkeyModifiers)
                 or nameof(MainWindowViewModel.JumpHotkeyKey)
                 or nameof(MainWindowViewModel.IsArrangeHotkeyEnabled)
                 or nameof(MainWindowViewModel.ArrangeHotkeyModifiers)
                 or nameof(MainWindowViewModel.ArrangeHotkeyKey))
        {
            // Settings-load assignments also land here; ApplyHotkeys is
            // idempotent, so the double-run at startup is harmless.
            ApplyHotkeys();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsAutoArrangeEnabled))
        {
            _autoArrangeWatcher.SetEnabled(_viewModel.IsAutoArrangeEnabled);
        }
    }

    private void OnGlobalThemeChanged(ThemeType currentTheme, System.Windows.Media.Color systemAccent)
    {
        // When the watcher changes the theme (system theme changed),
        // re-override if user has a manual theme selected
        var themeIndex = _viewModel.SelectedThemeIndex;
        if (themeIndex == 1 || themeIndex == 2)
        {
            // User wants a fixed theme, re-apply it
            Dispatcher.BeginInvoke(() => ApplyTheme(themeIndex));
        }
    }

    private void ApplyTheme(int themeIndex)
    {
        switch (themeIndex)
        {
            case 1: // Light
                Theme.Apply(ThemeType.Light, BackgroundType.Mica, true, true);
                break;
            case 2: // Dark
                Theme.Apply(ThemeType.Dark, BackgroundType.Mica, true, true);
                break;
            default: // System - detect current system theme and apply it
                var systemTheme = Theme.GetSystemTheme();
                var isDark = systemTheme is SystemThemeType.Dark
                    or SystemThemeType.CapturedMotion or SystemThemeType.Glow;
                Theme.Apply(isDark ? ThemeType.Dark : ThemeType.Light, BackgroundType.Mica, true, true);
                break;
        }
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon();

        // Previously this used Icon.ExtractAssociatedIcon(exePath), which pulls
        // a single arbitrary frame (typically the 32x32) from the exe's Win32
        // icon resource. The NotifyIcon then scaled that down to the system
        // tray size, producing a visibly smaller, fuzzier glyph than neighbour
        // icons that were authored at the correct pixel size.
        //
        // Load from the WPF-resource-packed app.ico instead and hand the Icon
        // ctor SystemInformation.SmallIconSize — that's the DPI-adjusted tray
        // size (16 at 100%, 20 at 125%, 24 at 150%, ...). The Icon(Stream,Size)
        // ctor picks the closest-matching frame from the multi-size file, so
        // the 16/20/24 frames we pack in app.ico all get used at their native
        // size with no resampling.
        try
        {
            _trayIcon.Icon = LoadTrayIconFromResources();
        }
        catch
        {
            // Any failure here (missing resource, exe rewritten in an odd way,
            // etc.) falls back to a neutral shell icon rather than crashing
            // the tray setup — the app still works without the tray glyph.
            _trayIcon.Icon = Drawing.SystemIcons.Application;
        }

        _trayIcon.Text = "SimpleScreen";
        _trayIcon.Visible = true;

        var menu = new Forms.ContextMenuStrip();

        // Kept as fields so a live language switch can retitle them: the tray
        // menu belongs to WinForms and never sees WPF's DynamicResource.
        _trayShowItem = new Forms.ToolStripMenuItem(
            LocalizationService.Get("Tray_Show"), null, (_, _) => ShowFromTray());
        _trayExitItem = new Forms.ToolStripMenuItem(
            LocalizationService.Get("Tray_Exit"), null, (_, _) => ForceClose());

        menu.Items.Add(_trayShowItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_trayExitItem);
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    /// <summary>
    /// Retitles the WinForms tray menu after a language switch. WPF-side text
    /// updates itself through DynamicResource; this is the one surface outside
    /// the WPF resource system.
    /// </summary>
    private void OnLanguageChanged()
    {
        if (_trayShowItem != null) _trayShowItem.Text = LocalizationService.Get("Tray_Show");
        if (_trayExitItem != null) _trayExitItem.Text = LocalizationService.Get("Tray_Exit");
    }

    /// <summary>
    /// Loads the multi-size app.ico (packed as a WPF resource via the csproj
    /// &lt;Resource&gt; item) and asks for the frame matching the current tray
    /// icon size. Done once at startup; the tray doesn't update the icon when
    /// DPI changes mid-session, which matches how every other tray app behaves.
    /// </summary>
    private static Drawing.Icon LoadTrayIconFromResources()
    {
        var uri = new Uri("pack://application:,,,/app.ico", UriKind.Absolute);
        var streamInfo = System.Windows.Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException("app.ico resource not found");
        using var stream = streamInfo.Stream;

        // SystemInformation.SmallIconSize is DPI-aware: returns 16x16 at 100%,
        // 20x20 at 125%, 24x24 at 150%. Our ICO contains exact matches for all
        // three so the Icon ctor returns a perfect, unscaled frame.
        var size = Forms.SystemInformation.SmallIconSize;
        return new Drawing.Icon(stream, size);
    }

    /// <summary>
    /// Brings the window back from the tray. Also used when the user launches
    /// the exe again while it's already running — in that case the window is
    /// usually hidden *and* possibly parked on a monitor that has since been
    /// unplugged, so the on-screen check runs every time.
    /// </summary>
    internal void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;

        // Show() only flips WPF's own Visibility flag; if the HWND was hidden by
        // anything else (a shell helper, remote desktop, our own diagnostics),
        // WPF considers it shown and the window stays invisible. Push it down to
        // Win32 as well so "Open" always opens something.
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && !NativeMethods.IsWindowVisible(handle))
            NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOW);

        if (WindowScaleService.EnsureOnScreen(this))
        {
            // Moved back onto a live monitor: re-apply the remembered size for
            // the DPI it landed on, same rule as at startup.
            if (_viewModel.IsWindowScaleSyncEnabled)
            {
                var (pixelWidth, pixelHeight) = _viewModel.RememberedWindowPixelSize;
                if (pixelWidth > 0 && pixelHeight > 0)
                    RestorePixelSize(pixelWidth, pixelHeight);
            }
        }

        Activate();
        Topmost = true;   // win32 foreground rules otherwise leave it behind
        Topmost = false;
        Focus();
    }

    private void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private void NavButton_Checked(object sender, RoutedEventArgs e)
    {
        // Guard: Checked fires during InitializeComponent before _pages is set
        if (_pages == null) return;

        if (sender is RadioButton rb && rb.Tag is string page
            && int.TryParse(page, out var index))
        {
            _viewModel.CurrentPage = index;
            ShowPage(index, animate: true);
        }
    }

    private void ShowPage(int index, bool animate)
    {
        if (index < 0 || index >= _pages.Length) return;

        var target = _pages[index];
        if (target == _activePage) return;

        // Hide the previous page instantly
        if (_activePage != null)
        {
            _activePage.Visibility = Visibility.Collapsed;
            _activePage.Opacity = 0;
        }

        // Show and animate the new page
        target.Visibility = Visibility.Visible;

        if (animate)
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(250));

            // Fade in
            var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
            target.BeginAnimation(OpacityProperty, fadeIn);

            // Slide up
            var transform = target.RenderTransform as TranslateTransform;
            if (transform == null)
            {
                transform = new TranslateTransform();
                target.RenderTransform = transform;
            }
            var slideUp = new DoubleAnimation(20, 0, duration) { EasingFunction = ease };
            transform.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }
        else
        {
            target.Opacity = 1;
        }

        _activePage = target;
    }

    // ── Per-app override (pin) UI handlers ────────────────────────────────
    //
    // The session-row ContextMenu is declared with two MenuItems by name
    // (PinToDeviceMenu, RemovePinMenu). On open we rebuild the "Pin to" submenu
    // from the live AudioDevices list and toggle "Remove pin" visibility based
    // on the row's IsOverridden state. The session VM is carried via the row
    // Border's Tag and read back through PlacementTarget.Tag — ContextMenu lives
    // in its own popup window and doesn't inherit the row's DataContext.

    /// <summary>
    /// Keyboard entry point for the session-row context menu. The row is
    /// <c>Focusable="True"</c> so Tab can reach it; this handler opens the same
    /// ContextMenu that right-click opens when the user presses the Apps key
    /// or Shift+F10 — matching standard Windows list-row keyboarding.
    /// </summary>
    private void SessionRow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu is not ContextMenu menu)
            return;

        bool shouldOpen = e.Key == Key.Apps
            || (e.Key == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        if (!shouldOpen) return;

        // Anchor the popup at the row and let ContextMenuOpening populate it,
        // so the keyboard path goes through the same flow as the mouse path.
        menu.PlacementTarget = fe;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void SessionRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.ContextMenu is not ContextMenu menu) return;
        if (fe.Tag is not SessionDisplayViewModel session)
        {
            e.Handled = true;
            return;
        }

        var pinSubmenu = menu.Items.OfType<WpfMenuItem>()
            .FirstOrDefault(m => m.Name == "PinToDeviceMenu");
        var removeItem = menu.Items.OfType<WpfMenuItem>()
            .FirstOrDefault(m => m.Name == "RemovePinMenu");
        var separator = menu.Items.OfType<Separator>()
            .FirstOrDefault(s => s.Name == "PinSeparator");

        if (pinSubmenu == null || removeItem == null || separator == null) return;

        // Rebuild the device submenu fresh each open — devices can be hot-plugged
        // between menu uses, and the user's currently-pinned device should appear
        // checked even if it's not the row's currently-routed device.
        pinSubmenu.Items.Clear();

        // Skip the synthetic "(No mapping)" entry — its Id is empty and pinning
        // to it would mean "no device", which the override schema doesn't model
        // (use Remove pin for that).
        var devices = _viewModel.AudioDevices.Where(d => !string.IsNullOrEmpty(d.Id)).ToList();
        if (devices.Count == 0)
        {
            pinSubmenu.Items.Add(new WpfMenuItem { Header = LocalizationService.Get("Text_NoAudioDevices"), IsEnabled = false });
        }
        else
        {
            // Find the existing pin (if any) so we can show a check next to the
            // device the user already chose for this app.
            var existingPin = _viewModel.AppOverrides.FirstOrDefault(
                o => string.Equals(o.ProcessName, session.ProcessName,
                                   StringComparison.OrdinalIgnoreCase));

            foreach (var device in devices)
            {
                var item = new WpfMenuItem
                {
                    Header = device.FriendlyName,
                    Tag = device.Id,
                    IsCheckable = true,
                    IsChecked = existingPin != null && existingPin.AudioDeviceId == device.Id,
                };
                item.Click += PinToDevice_Click;
                pinSubmenu.Items.Add(item);
            }
        }

        // Hide remove pin (and its separator) when there's nothing to remove.
        var hasPin = session.IsOverridden ||
                     _viewModel.AppOverrides.Any(o => string.Equals(
                         o.ProcessName, session.ProcessName, StringComparison.OrdinalIgnoreCase));
        removeItem.Visibility = hasPin ? Visibility.Visible : Visibility.Collapsed;
        separator.Visibility = hasPin ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PinToDevice_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem item) return;
        if (item.Tag is not string deviceId || string.IsNullOrEmpty(deviceId)) return;

        // Walk up to the owning ContextMenu to recover the row's session VM.
        var menu = ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu
                   ?? FindParentContextMenu(item);
        if (menu?.PlacementTarget is FrameworkElement fe && fe.Tag is SessionDisplayViewModel session)
        {
            // Carry the session's already-extracted icon straight over to the
            // pin so the Pinned page shows it without waiting for the next
            // sessions update.
            _viewModel.SetAppOverride(session.ProcessName, deviceId, session.Icon);
        }
    }

    private void RemovePin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem item) return;
        var menu = FindParentContextMenu(item);
        if (menu?.PlacementTarget is FrameworkElement fe && fe.Tag is SessionDisplayViewModel session)
        {
            _viewModel.RemoveAppOverride(session.ProcessName);
        }
    }

    // Walks up logical/visual parents to find the enclosing ContextMenu. Needed
    // because ItemsControlFromItemContainer doesn't reach across submenu levels.
    private static ContextMenu? FindParentContextMenu(DependencyObject start)
    {
        DependencyObject? current = start;
        while (current != null)
        {
            if (current is ContextMenu cm) return cm;
            current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void RemovePinFromList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is AppOverrideViewModel ovr)
        {
            _viewModel.RemoveAppOverride(ovr.ProcessName);
        }
    }

    // ── Hotkeys page: global jump hotkey ──────────────────────────────────

    /// <summary>
    /// Routes WM_HOTKEY to the right action. JumpHotkeyId throws the foreground
    /// window onto the other display; ArrangeHotkeyId tiles every window on the
    /// secondary display. Other messages pass through untouched.
    /// </summary>
    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;

        var id = wParam.ToInt64();

        // Last line of defence for bare keys only: if a caret appeared after
        // the focus event but before this key press, refuse to act. A modifier
        // combo cannot be a typed character, so it always runs.
        var bare = id == JumpHotkeyId
            ? _viewModel.JumpHotkeyModifiers == 0
            : id == ArrangeHotkeyId && _viewModel.ArrangeHotkeyModifiers == 0;
        if (bare && TextInputGuard.IsTextInputActive())
        {
            handled = true;
            return IntPtr.Zero;
        }
        if (id == JumpHotkeyId)
        {
            handled = true;
            WindowJumpService.MoveForegroundToOtherMonitor();
            // SetWindowPos doesn't raise EVENT_SYSTEM_MOVESIZEEND, so the
            // routing engine wouldn't see the window's new monitor until its
            // 30s heartbeat. Kick an immediate recheck so the app's audio
            // follows the window right away instead of a noticeable delay.
            _viewModel.RequestAudioRouteRecheck();
        }
        else if (id == ArrangeHotkeyId)
        {
            handled = true;
            _autoArrangeWatcher.ArrangeNow();
            // Tiling also relocates windows across monitors — recheck audio too.
            _viewModel.RequestAudioRouteRecheck();
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Re-registers both global hotkeys from the current view-model settings.
    /// Unregister-first keeps the call idempotent; MOD_NOREPEAT stops key
    /// auto-repeat from machine-gunning the action. While a key is being
    /// recorded both registrations stay released so the pressed combo reaches
    /// the WPF tunnelling handler instead of firing the hotkey.
    /// </summary>
    /// <summary>
    /// Called on every foreground/focus change. Only re-registers when the
    /// "typing" state actually flips, so the frequent EVENT_OBJECT_FOCUS
    /// notifications cost almost nothing.
    /// </summary>
    private void ReevaluateHotkeys()
    {
        if (_hotkeyHwnd == IntPtr.Zero) return;
        if (_recordingTarget != 0) return; // recording owns registration
        var typing = TextInputGuard.IsTextInputActive();
        if (typing == _typingActive) return;
        ApplyHotkeys();
    }

    private void ApplyHotkeys()
    {
        if (_hotkeyHwnd == IntPtr.Zero) return;

        if (_jumpRegistered)
        {
            NativeMethods.UnregisterHotKey(_hotkeyHwnd, JumpHotkeyId);
            _jumpRegistered = false;
        }
        if (_arrangeRegistered)
        {
            NativeMethods.UnregisterHotKey(_hotkeyHwnd, ArrangeHotkeyId);
            _arrangeRegistered = false;
        }
        // Recording needs both combos released so the pressed key reaches the
        // WPF recorder. Otherwise each registration decides for itself: a bare
        // key steps aside while a text caret is active; a modifier combo stays
        // live because it can never be a typed character.
        _typingActive = TextInputGuard.IsTextInputActive();
        if (_recordingTarget != 0)
        {
            _viewModel.HotkeyStatus = string.Empty;
            _viewModel.ArrangeHotkeyStatus = string.Empty;
            return;
        }

        RegisterJumpHotkey();
        RegisterArrangeHotkey();
    }

    private void RegisterJumpHotkey()
    {
        if (!_viewModel.IsJumpHotkeyEnabled || _viewModel.JumpHotkeyKey <= 0)
        {
            _viewModel.HotkeyStatus = string.Empty;
            return;
        }

        // A bare key is the only case that can swallow a typed letter. Release
        // it while the foreground app owns a text caret.
        if (_viewModel.JumpHotkeyModifiers == 0 && _typingActive)
        {
            _viewModel.HotkeyStatus = string.Empty;
            return;
        }

        var modifiers = (uint)_viewModel.JumpHotkeyModifiers | NativeMethods.MOD_NOREPEAT;
        if (NativeMethods.RegisterHotKey(_hotkeyHwnd, JumpHotkeyId, modifiers, (uint)_viewModel.JumpHotkeyKey))
        {
            _jumpRegistered = true;
            _viewModel.HotkeyStatus = string.Empty;
        }
        else
        {
            _viewModel.HotkeyStatus = LocalizationService.Get("HK_RegisterFailed");
        }
    }

    private void RegisterArrangeHotkey()
    {
        if (!_viewModel.IsArrangeHotkeyEnabled || _viewModel.ArrangeHotkeyKey <= 0)
        {
            _viewModel.ArrangeHotkeyStatus = string.Empty;
            return;
        }

        if (_viewModel.ArrangeHotkeyModifiers == 0 && _typingActive)
        {
            _viewModel.ArrangeHotkeyStatus = string.Empty;
            return;
        }

        var modifiers = (uint)_viewModel.ArrangeHotkeyModifiers | NativeMethods.MOD_NOREPEAT;
        if (NativeMethods.RegisterHotKey(_hotkeyHwnd, ArrangeHotkeyId, modifiers, (uint)_viewModel.ArrangeHotkeyKey))
        {
            _arrangeRegistered = true;
            _viewModel.ArrangeHotkeyStatus = string.Empty;
        }
        else
        {
            _viewModel.ArrangeHotkeyStatus = LocalizationService.Get("HK_RegisterFailed");
        }
    }

    /// <summary>
    /// The long descriptions on the hotkeys page stay collapsed behind a small
    /// chevron. These handlers flip the panel and the glyph together.
    /// </summary>
    private void MoveDetailsButton_Click(object sender, RoutedEventArgs e) =>
        ToggleHotkeyDetails(MoveDetailsPanel, MoveDetailsChevron);

    private void ArrangeDetailsButton_Click(object sender, RoutedEventArgs e) =>
        ToggleHotkeyDetails(ArrangeDetailsPanel, ArrangeDetailsChevron);

    private static void ToggleHotkeyDetails(System.Windows.UIElement panel,
                                            System.Windows.Controls.TextBlock chevron)
    {
        bool expanded = panel.Visibility != Visibility.Visible;
        panel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        chevron.Text = expanded ? "\uE70E" : "\uE70D";
    }

    // ---- Key-cell capture -------------------------------------------------
    // Each hotkey has two independent cells. _recordingTarget picks the
    // feature (1 = jump, 2 = arrange); _recordingSlot picks the cell
    // (0 = modifier, 1 = main key). Only the armed cell is highlighted, and
    // only that cell is written when a key arrives — editing one cell never
    // disturbs the other.

    private void JumpModifierBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => BeginHotkeyRecording(1, 0);

    private void JumpKeyBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => BeginHotkeyRecording(1, 1);

    private void ArrangeModifierBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => BeginHotkeyRecording(2, 0);

    private void ArrangeKeyBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => BeginHotkeyRecording(2, 1);

    private void BeginHotkeyRecording(int target, int slot)
    {
        _recordingTarget = target;
        _recordingSlot = slot;
        _pendingModifiers = 0;

        ClearKeyBoxHighlights();
        var armed = ArmedBox();
        armed.BorderBrush = (System.Windows.Media.Brush)FindResource("SystemAccentColorPrimaryBrush");
        armed.BorderThickness = new Thickness(2);

        var status = target == 1 ? HotkeyRecordStatus : ArrangeRecordStatus;
        status.Text = LocalizationService.Get(slot == 0 ? "HK_ModifierRecording" : "HK_KeyRecording");
        status.Visibility = Visibility.Visible;
        (target == 1 ? ArrangeRecordStatus : HotkeyRecordStatus).Visibility = Visibility.Collapsed;

        ApplyHotkeys();   // release both OS hotkeys so the pressed key reaches WPF
        HotkeyPage.Focus();
    }

    private Border ArmedBox() => (_recordingTarget, _recordingSlot) switch
    {
        (1, 0) => JumpModifierBox,
        (1, 1) => JumpKeyBox,
        (2, 0) => ArrangeModifierBox,
        _ => ArrangeKeyBox,
    };

    /// <summary>
    /// Restore the theme stroke on all four cells. SetResourceReference puts
    /// the DynamicResource binding back (a plain ClearValue would leave the
    /// border with no brush at all after a language/theme swap).
    /// </summary>
    private void ClearKeyBoxHighlights()
    {
        foreach (var box in new[] { JumpModifierBox, JumpKeyBox, ArrangeModifierBox, ArrangeKeyBox })
        {
            box.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            box.BorderThickness = new Thickness(1);
        }
    }

    /// <summary>
    /// Tunnelling handler on the whole hotkeys page. Modifier flags are read
    /// from Keyboard.Modifiers, whose enum values (Alt=1, Control=2, Shift=4,
    /// Windows=8) are identical to the Win32 MOD_* bits used by RegisterHotKey,
    /// so the int can be stored directly in settings.
    /// </summary>
    private void HotkeyPage_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_recordingTarget == 0) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { StopHotkeyRecording(); return; }

        bool isModifier = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                              or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

        _pendingModifiers |= (int)Keyboard.Modifiers;

        if (_recordingSlot == 0)
        {
            // Modifier cell: modifier presses accumulate; commit on the next
            // normal key, or when every modifier has been released (PreviewKeyUp).
            if (isModifier) return;
            if (key is Key.Back or Key.Delete) _pendingModifiers = 0;
            CommitModifierCell();
            return;
        }

        // Main-key cell: wait through modifier presses, then take the first real
        // key. Held modifiers are deliberately ignored so this cell never
        // rewrites its neighbour.
        if (isModifier) return;
        if (key is Key.Back or Key.Delete) { StopHotkeyRecording(); return; }

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0) { StopHotkeyRecording(); return; }
        if (_recordingTarget == 1) _viewModel.JumpHotkeyKey = vk;
        else _viewModel.ArrangeHotkeyKey = vk;
        StopHotkeyRecording();
    }

    /// <summary>
    /// A pure modifier gesture (e.g. hold Ctrl+Alt, then let go) has no normal
    /// KeyDown to commit on, so KeyUp with no modifiers left ends the capture.
    /// </summary>
    private void HotkeyPage_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_recordingTarget == 0 || _recordingSlot != 0) return;
        if (Keyboard.Modifiers == ModifierKeys.None && _pendingModifiers != 0)
            CommitModifierCell();
    }

    private void CommitModifierCell()
    {
        if (_recordingTarget == 1) _viewModel.JumpHotkeyModifiers = _pendingModifiers;
        else _viewModel.ArrangeHotkeyModifiers = _pendingModifiers;
        StopHotkeyRecording();
    }

    /// <summary>
    /// Ends capture: hides the hint, restores all four borders and re-registers
    /// both combos (a cancelled recording changed nothing, so registration
    /// still needs doing even though ViewModel setters did not fire).
    /// </summary>
    private void StopHotkeyRecording()
    {
        _recordingTarget = 0;
        _recordingSlot = 0;
        _pendingModifiers = 0;
        HotkeyRecordStatus.Visibility = Visibility.Collapsed;
        ArrangeRecordStatus.Visibility = Visibility.Collapsed;
        ClearKeyBoxHighlights();
        ApplyHotkeys();
    }


    /// <summary>
    /// Cancel an in-flight recording when the window loses activation —
    /// otherwise the recorder sits armed forever while the user alt-tabs away
    /// mid-thought and the next unrelated keystroke becomes the hotkey.
    /// </summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_recordingTarget != 0) StopHotkeyRecording();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose && _viewModel.MinimizeOnClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Theme.Changed -= OnGlobalThemeChanged;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _scaleTimer.Stop();

        _autoArrangeTimer.Stop();
        _autoArrangeWatcher.SetEnabled(false);
        _hotkeyGuardTimer.Stop();
        _textInputGuard.Dispose();

        // Release both global hotkeys explicitly (destroying the HWND would
        // also unregister them, but being tidy costs a few lines).
        if (_jumpRegistered)
        {
            NativeMethods.UnregisterHotKey(_hotkeyHwnd, JumpHotkeyId);
            _jumpRegistered = false;
        }
        if (_arrangeRegistered)
        {
            NativeMethods.UnregisterHotKey(_hotkeyHwnd, ArrangeHotkeyId);
            _arrangeRegistered = false;
        }

        // Remember the physical size so the next launch lands on the same
        // on-screen size even if the app starts on a different-DPI monitor
        // (see OnDpiChanged). Skipped while maximised: that's a state, not a
        // size, and the restore size is what we want to keep.
        if (WindowState == WindowState.Normal)
        {
            var (pixelWidth, pixelHeight) = WindowScaleService.GetPixelSize(this);
            _viewModel.RememberWindowSize(
                (int)Math.Round(pixelWidth),
                (int)Math.Round(pixelHeight));
        }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
