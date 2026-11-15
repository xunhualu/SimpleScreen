using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using System.Windows.Threading;
using ScreenSound.Models;
using ScreenSound.Services;

namespace ScreenSound.ViewModels;

/// <summary>
/// One generic DDC/CI row on a screen-details card (red/green/blue gain,
/// sharpness, mute, scene mode …). Built only for codes the panel actually
/// accepted during discovery, so the page never shows a knob that does nothing.
/// </summary>
public partial class VcpControlViewModel : ObservableObject
{
    private readonly MonitorInfo _monitor;
    private readonly VcpService _vcp;
    private readonly Action<string> _reportStatus;
    private readonly DispatcherTimer? _debounce;
    private readonly string _labelKey;
    private bool _loading;

    public byte Code { get; }
    public bool IsChoice { get; }

    /// <summary>Optional per-row accent (red/green/blue gain rows are tinted).</summary>
    public Color? AccentColor { get; }
    public Brush? AccentBrush { get; }

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private double _value;
    [ObservableProperty] private double _maxValue = 100;
    [ObservableProperty] private string _valueText = "";
    [ObservableProperty] private VcpOption? _selectedOption;

    public ObservableCollection<VcpOption> Options { get; } = new();

    public Visibility SliderVisibility => IsChoice ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ChoiceVisibility => IsChoice ? Visibility.Visible : Visibility.Collapsed;

    private VcpControlViewModel(MonitorInfo monitor, VcpService vcp, byte code, string labelKey,
        bool isChoice, Action<string> reportStatus, Color? accent = null)
    {
        _monitor = monitor;
        _vcp = vcp;
        _reportStatus = reportStatus;
        Code = code;
        IsChoice = isChoice;
        _labelKey = labelKey;
        Label = LocalizationService.Get(labelKey);
        AccentColor = accent;
        AccentBrush = accent.HasValue ? new SolidColorBrush(accent.Value) : null;

        if (!isChoice)
        {
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            _debounce.Tick += async (_, _) =>
            {
                _debounce!.Stop();
                await WriteAsync((uint)Value, verify: false);
            };
        }
    }

    public static VcpControlViewModel Slider(MonitorInfo monitor, VcpService vcp, byte code,
        string labelKey, uint current, uint max, Action<string> reportStatus, Color? accent = null)
    {
        var vm = new VcpControlViewModel(monitor, vcp, code, labelKey, false, reportStatus, accent);
        vm.MaxValue = max == 0 ? 100 : max;
        vm._loading = true;
        vm.Value = Math.Min(current, vm.MaxValue);
        vm._loading = false;
        vm.ValueText = ((int)vm.Value).ToString();
        return vm;
    }

    public static VcpControlViewModel Choice(MonitorInfo monitor, VcpService vcp, byte code,
        string labelKey, IEnumerable<VcpOption> options, uint current, Action<string> reportStatus)
    {
        var vm = new VcpControlViewModel(monitor, vcp, code, labelKey, true, reportStatus);
        foreach (var o in options) vm.Options.Add(o);
        vm._loading = true;
        vm.SelectedOption = vm.Options.FirstOrDefault(o => o.Value == current);
        vm._loading = false;
        vm.ValueText = "";
        return vm;
    }

    public void RefreshLocalizedText() => Label = LocalizationService.Get(_labelKey);

    partial void OnValueChanged(double value)
    {
        ValueText = ((int)value).ToString();
        if (_loading) return;
        _debounce?.Stop();
        _debounce?.Start();
    }

    partial void OnSelectedOptionChanged(VcpOption? value)
    {
        if (_loading || value == null) return;
        _ = WriteAsync(value.Value, verify: true);
    }

    private async Task WriteAsync(uint value, bool verify)
    {
        var ok = await _vcp.SetVcpAsync(_monitor, Code, value);
        if (!ok)
        {
            _reportStatus("writefail");
            return;
        }

        if (!verify) { _reportStatus(""); return; }

        // Discrete values on cheap panels silently snap back — surface the real
        // state instead of leaving a dropdown showing a lie.
        await Task.Delay(320);
        var back = await _vcp.GetVcpAsync(_monitor, Code);
        if (!back.HasValue || back.Value.Current == value) { _reportStatus(""); return; }

        _loading = true;
        SelectedOption = Options.FirstOrDefault(o => o.Value == back.Value.Current);
        _loading = false;
        _reportStatus("notsupported");
    }
}
