using ScreenSound.Interop;
using System.Diagnostics;

namespace ScreenSound.Services;

/// <summary>
/// Software color adjustment for a single display, done through the GDI gamma
/// ramp. Panels whose firmware only answers a handful of DDC codes (or none at
/// all) still get a continuous color-temperature and low-blue-light control
/// this way — the values are applied to that monitor's output only.
///
/// 6500 K + 0 % eye-care is the identity ramp, i.e. "as the panel ships".
/// </summary>
public static class ColorGainService
{
    public const int NeutralKelvin = 6500;
    public const int MinKelvin = 4000;
    public const int MaxKelvin = 9300;

    /// <summary>Push the ramp for one display. Returns false when the DC/ramp call fails.</summary>
    public static bool Apply(string deviceName, int kelvin, int eyeCare)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return false;
        var hdc = IntPtr.Zero;
        try
        {
            hdc = GammaInterop.CreateDC("DISPLAY", deviceName, IntPtr.Zero, IntPtr.Zero);
            if (hdc == IntPtr.Zero) return false;
            return GammaInterop.SetDeviceGammaRamp(hdc, BuildRamp(kelvin, eyeCare));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ColorGain] apply failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (hdc != IntPtr.Zero) GammaInterop.DeleteDC(hdc);
        }
    }

    /// <summary>Back to the panel's own color (identity ramp).</summary>
    public static bool Reset(string deviceName) => Apply(deviceName, NeutralKelvin, 0);

    /// <summary>16-bit-per-channel ramp: linear identity curve scaled by the RGB factors.</summary>
    public static ushort[] BuildRamp(int kelvin, int eyeCare)
    {
        var (r, g, b) = Multipliers(kelvin, eyeCare);
        var ramp = new ushort[3 * 256];
        for (var i = 0; i < 256; i++)
        {
            var v = i / 255.0;
            ramp[i] = ToU16(v * r);
            ramp[256 + i] = ToU16(v * g);
            ramp[512 + i] = ToU16(v * b);
        }
        return ramp;
    }

    private static ushort ToU16(double value) => (ushort)Math.Clamp(value * 65535.0, 0, 65535);

    /// <summary>
    /// RGB multipliers relative to 6500 K, plus the blue-reduction curve for
    /// eye-care. Everything is normalised so the brightest channel stays at
    /// 1.0 — the ramp can only attenuate, never over-drive the panel.
    /// </summary>
    public static (double R, double G, double B) Multipliers(int kelvin, int eyeCare)
    {
        var k = Math.Clamp(kelvin, MinKelvin, MaxKelvin);
        var (r, g, b) = KelvinToRgb(k);
        var (nr, ng, nb) = KelvinToRgb(NeutralKelvin);
        double rr = r / nr, gg = g / ng, bb = b / nb;

        var peak = Math.Max(1.0, Math.Max(rr, Math.Max(gg, bb)));
        rr /= peak; gg /= peak; bb /= peak;

        var p = Math.Clamp(eyeCare, 0, 100) / 100.0;
        bb *= 1 - 0.55 * p;   // the wavelength that keeps the brain awake
        gg *= 1 - 0.12 * p;   // a touch of green keeps skin tones sane
        return (rr, gg, bb);
    }

    /// <summary>Tanner Helland's blackbody approximation, scaled to 0–255.</summary>
    private static (double R, double G, double B) KelvinToRgb(double kelvin)
    {
        var t = kelvin / 100.0;

        double r;
        if (t <= 66) r = 255;
        else r = 329.698727446 * Math.Pow(t - 60, -0.1332047592);

        double g;
        if (t <= 66) g = 99.4708025861 * Math.Log(t) - 161.1195681661;
        else g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492);

        double b;
        if (t >= 66) b = 255;
        else if (t <= 19) b = 0;
        else b = 138.5177312231 * Math.Log(t - 10) - 305.0447927307;

        return (Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
    }
}
