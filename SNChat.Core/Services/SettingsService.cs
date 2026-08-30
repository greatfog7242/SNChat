using System.Text.Json;
using SNChat.Core.Models;

namespace SNChat.Core.Services;

public class SettingsService
{
    private readonly string _settingsPath;
    private AppSettings? _cachedSettings;

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configDir = Path.Combine(appData, "SNChat", "config");
        Directory.CreateDirectory(configDir);
        _settingsPath = Path.Combine(configDir, "settings.json");
    }

    public async Task<AppSettings> LoadSettingsAsync()
    {
        if (_cachedSettings != null)
            return _cachedSettings;

        if (!File.Exists(_settingsPath))
        {
            _cachedSettings = new AppSettings();
            await SaveSettingsAsync(_cachedSettings);
            return _cachedSettings;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_settingsPath);
            _cachedSettings = JsonSerializer.Deserialize<AppSettings>(json,
                new JsonSerializerOptions { WriteIndented = true }) ?? new AppSettings();
            return _cachedSettings;
        }
        catch
        {
            _cachedSettings = new AppSettings();
            return _cachedSettings;
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        _cachedSettings = settings;
        var json = JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_settingsPath, json);
    }

    public AppSettings GetCachedSettings()
    {
        return _cachedSettings ?? new AppSettings();
    }
}
