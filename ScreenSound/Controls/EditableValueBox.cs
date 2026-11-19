using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenSound.Controls;

/// <summary>
/// A compact value readout that can also be typed into. It looks like the
/// plain "45%" / "6500K" label it replaces, but focuses on click and lets the
/// user type a number: Enter or losing focus commits, Escape reverts. The
/// typed value is clamped to [Minimum, Maximum] and pushed back through the
/// normal two-way binding, so the slider, the hardware write and any colour
/// sync all react exactly as if the slider had been dragged.
/// </summary>
public sealed class EditableValueBox : System.Windows.Controls.TextBox
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(EditableValueBox),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(EditableValueBox),
        new PropertyMetadata(0d, OnRangeOrFormatChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(EditableValueBox),
        new PropertyMetadata(100d, OnRangeOrFormatChanged));

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(EditableValueBox),
        new PropertyMetadata("%", OnRangeOrFormatChanged));

    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(
        nameof(Decimals), typeof(int), typeof(EditableValueBox),
        new PropertyMetadata(0, OnRangeOrFormatChanged));

    /// <summary>Bound to the slider's value. Two-way by default.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>Text appended to the number, e.g. "%" or "K". Empty for none.</summary>
    public string Suffix
    {
        get => (string)GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    public int Decimals
    {
        get => (int)GetValue(DecimalsProperty);
        set => SetValue(DecimalsProperty, value);
    }

    private bool _editing;

    static EditableValueBox()
    {
        // Use the stock TextBox template/theme, just a different managed type.
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(EditableValueBox), new FrameworkPropertyMetadata(typeof(System.Windows.Controls.TextBox)));
    }

    public EditableValueBox()
    {
        IsTabStop = true;
        TextAlignment = TextAlignment.Right;
        VerticalContentAlignment = VerticalAlignment.Center;
        // Render once the template is ready (min/max/suffix bindings may arrive
        // after construction, which is handled by OnRangeOrFormatChanged).
        Loaded += (_, _) => FormatText();
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewTextInput += OnPreviewTextInput;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (EditableValueBox)d;
        if (!box._editing)
            box.FormatText();
    }

    private static void OnRangeOrFormatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (EditableValueBox)d;
        if (!box._editing)
            box.FormatText();
    }

    private string Format(double value) =>
        Decimals > 0
            ? value.ToString($"F{Decimals}", CultureInfo.InvariantCulture) + Suffix
            : ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture) + Suffix;

    private void FormatText()
    {
        Text = Format(Value);
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        _editing = true;
        // Show the bare number while editing so the user can type straight over
        // it without having to delete the unit suffix.
        Text = Value.ToString(Decimals > 0 ? $"F{Decimals}" : "0", CultureInfo.InvariantCulture);
        SelectAll();
    }

    private void OnLostFocus(object sender, RoutedEventArgs e) => Commit();

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Discard the edit and restore the bound value.
            _editing = false;
            FormatText();
            e.Handled = true;
        }
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Digits and one decimal separator only (when decimals are allowed).
        // Minus is not needed: every slider here is non-negative.
        foreach (var ch in e.Text)
        {
            var ok = char.IsDigit(ch) ||
                     (Decimals > 0 && ch == '.' && !Text.Contains('.'));
            if (!ok)
            {
                e.Handled = true;
                return;
            }
        }
    }

    private void Commit()
    {
        if (!_editing) return;
        _editing = false;

        if (double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            var clamped = Math.Clamp(parsed, Minimum, Maximum);
            // Two-way binding carries this to the VM (and from there to the
            // slider / hardware). Assigning the same value is a no-op.
            Value = clamped;
        }

        FormatText();
    }
}
