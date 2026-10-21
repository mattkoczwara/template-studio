using System.Text.Json;
using Mosaic.Core;

namespace Mosaic.Storage;

public sealed record AppPreferences
{
    public int SchemaVersion { get; init; } = 1;
    public string Language { get; init; } = "en";
    public List<string> RecentProjects { get; init; } = [];
    public List<CalibrationRecord> Calibrations { get; init; } = [];
    public bool CalibrationPromptDismissed { get; init; }
}

public sealed class PreferencesStore
{
    private readonly object _gate = new();
    public string RootPath { get; }
    public string RecoveryPath => Path.Combine(RootPath, "recovery.mosaic");
    private string SettingsPath => Path.Combine(RootPath, "settings.json");
    public PreferencesStore(string? root = null) => RootPath = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MosaicTemplateStudio");

    public AppPreferences Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new();
                if (new FileInfo(SettingsPath).Length > 1024 * 1024) throw new InvalidDataException();
                var prefs = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(SettingsPath), ProjectStore.JsonOptions);
                if (prefs is null || prefs.SchemaVersion != 1 || prefs.Language is not ("en" or "pl") ||
                    prefs.RecentProjects is null || prefs.Calibrations is null) throw new InvalidDataException();
                return prefs with
                {
                    RecentProjects = prefs.RecentProjects.Where(p => !string.IsNullOrWhiteSpace(p) && p.Length < 32768)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList(),
                    Calibrations = prefs.Calibrations.Where(IsValidCalibration).Take(32).ToList()
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // Retain damaged preferences for recovery; never damage personal projects.
                try
                {
                    if (File.Exists(SettingsPath)) File.Copy(SettingsPath, SettingsPath + ".damaged", overwrite: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return new();
            }
        }
    }

    public void Save(AppPreferences preferences)
    {
        if (preferences.Language is not ("en" or "pl")) throw new ArgumentOutOfRangeException(nameof(preferences));
        lock (_gate)
        {
            Directory.CreateDirectory(RootPath);
            string temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var safe = preferences with
                {
                    SchemaVersion = 1,
                    RecentProjects = preferences.RecentProjects.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList(),
                    Calibrations = preferences.Calibrations.Where(IsValidCalibration).Take(32).ToList()
                };
                File.WriteAllText(temporary, JsonSerializer.Serialize(safe, ProjectStore.JsonOptions));
                File.Move(temporary, SettingsPath, overwrite: true);
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
        }
    }

    public AppPreferences AddRecent(string path)
    {
        lock (_gate)
        {
            var current = Load();
            string absolute = Path.GetFullPath(path);
            current = current with { RecentProjects = new[] { absolute }.Concat(current.RecentProjects)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList() };
            Save(current);
            return current;
        }
    }

    private static bool IsValidCalibration(CalibrationRecord calibration) => calibration is not null &&
        calibration.MeasuredXInches is >= 3m and <= 5m && calibration.MeasuredYInches is >= 3m and <= 5m &&
        calibration.OffsetXInches is >= -1m and <= 1m && calibration.OffsetYInches is >= -1m and <= 1m &&
        calibration.Pathway is "direct" or "pdf" && Enum.IsDefined(calibration.Orientation) &&
        calibration.PrinterName is not null && calibration.PrinterName.Length <= 1024 &&
        calibration.DriverSettings is not null && calibration.DriverSettings.Length <= 4096 &&
        calibration.Media is not null && calibration.Media.Length <= 1024 &&
        calibration.Driver is not null && calibration.Driver.Length <= 1024;
}
