using WindowsDisplayAPI.DisplayConfig;
using WindowsDisplayAPI.Native.DisplayConfig;

namespace ScreenSound.Services;

/// <summary>
/// Display rotation through the CCD API (SetDisplayConfig) — the same code
/// path the Windows Settings app uses. The classic ChangeDisplaySettingsEx
/// route only works when the driver's mode list contains pre-swapped portrait
/// modes, which cheap portable-panel drivers and the hybrid-graphics stack on
/// this machine never expose (every 90°/270° attempt returns DISP_CHANGE_BADMODE),
/// while CCD accepts rotation on the very same panels.
/// </summary>
public static class DisplayRotationService
{
    /// <summary>
    /// Rotates one active display. Returns null if the display could not be
    /// matched (caller should fall back to the GDI route), true on success
    /// and false when the system rejected the request.
    /// </summary>
    public static bool? TryRotate(string deviceName, bool portrait) =>
        TryRotate(new[] { deviceName }, portrait);

    /// <summary>
    /// Rotate every active path whose GDI source name is in <paramref name="deviceNames"/>.
    /// A dual-link panel is two GDI devices for one physical screen; rotating only
    /// the card's own link does nothing when the panel is showing the other input.
    /// CCD is a global commit, so all matching links are rebuilt in ONE ApplyPathInfos
    /// call — calling it twice would rebuild from a stale snapshot and undo the first.
    /// </summary>
    public static bool? TryRotate(IEnumerable<string> deviceNames, bool portrait)
    {
        try
        {
            var names = new HashSet<string>(deviceNames, StringComparer.OrdinalIgnoreCase);
            var rotation = portrait ? DisplayConfigRotation.Rotate90 : DisplayConfigRotation.Identity;

            var paths = PathInfo.GetActivePaths(false).ToList();
            if (!paths.Any(p => names.Contains(p.DisplaySource.DisplayName ?? ""))) return null;

            // Rebuild every path; each matching one gets a fresh target info that
            // keeps the signal AND desktop-image data — dropping the desktop
            // image information makes the whole set invalid. Non-matching paths
            // are rebuilt unchanged (ApplyPathInfos needs the whole set).
            var rebuilt = paths.Select(p =>
            {
                if (!names.Contains(p.DisplaySource.DisplayName ?? "")) return p;
                var targets = p.TargetsInfo.Select(t =>
                    t.IsDesktopImageInformationAvailable
                        ? new PathTargetInfo(t.DisplayTarget, t.SignalInfo, t.DesktopImage, rotation, t.Scaling)
                        : new PathTargetInfo(t.DisplayTarget, t.SignalInfo, rotation, t.Scaling)).ToArray();

                return new PathInfo(p.DisplaySource, p.Position, p.Resolution, p.PixelFormat, targets);
            }).ToList();

            PathInfo.ApplyPathInfos(rebuilt);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
