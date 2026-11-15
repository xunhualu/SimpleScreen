using ScreenSound.Models;
using System.IO;
using System.Text.Json;

namespace ScreenSound.Services;

public class SettingsService
{
    /// <summary>Where settings.json and brightness.log live.</summary>
    public static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SimpleScreen");

    // Builds before the SimpleScreen rebrand stored everything here. Migrated
    // once on first load so existing users keep every mapping and pin.
    private static readonly string LegacySettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ScreenSound");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public AppSettings Load()
    {
        MigrateLegacyData();
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
        }

        return new AppSettings();
    }

    /// <summary>
    /// One-time move of the pre-rename %APPDATA%\ScreenSound folder into the
    /// new %APPDATA%\SimpleScreen location. Skipped entirely when the new
    /// folder already has content (fresh install or migration already done).
    /// </summary>
    private static void MigrateLegacyData()
    {
        try
        {
            if (!Directory.Exists(LegacySettingsDir)) return;
            if (Directory.Exists(SettingsDir) && Directory.EnumerateFileSystemEntries(SettingsDir).Any()) return;

            Directory.Move(LegacySettingsDir, SettingsDir);
        }
        catch (Exception ex)
        {
            // Worst case the user re-creates their mappings — never block startup.
            System.Diagnostics.Debug.WriteLine($"Settings migration failed: {ex.Message}");
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);

            // Atomic-ish replace: write a temp file in the same directory, then
            // swap it over the real one. A crash mid-write previously left a
            // truncated settings.json behind and the next launch silently fell
            // back to defaults — every mapping and pin gone. With the swap, the
            // old file stays intact until the new one is fully on disk.
            var tempFile = SettingsFile + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }
}
