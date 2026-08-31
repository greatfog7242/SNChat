using System.Text.Json;
using SNChat.Core.Models;

namespace SNChat.Core.Services;

public class SettingsService
{
    /// <summary>
    /// Settings are hand-edited to configure things the UI does not expose yet,
    /// such as MCP servers. Case-insensitive matching means a hand-written
    /// "mcpServers" binds as readily as the "McpServers" we serialize, instead
    /// of silently deserializing to the default and looking like no config at all.
    /// </summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

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
            _cachedSettings = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions)
                ?? new AppSettings();
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
        var json = JsonSerializer.Serialize(settings, WriteOptions);
        await File.WriteAllTextAsync(_settingsPath, json);
    }

    public AppSettings GetCachedSettings()
    {
        return _cachedSettings ?? new AppSettings();
    }
}
