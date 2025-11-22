using System.IO;
using Newtonsoft.Json;

namespace TabbySSH.Services;

public class ConfigurationManager
{
    private const string APP_NAME = "TabbySSH";
    private const string SESSIONS_FILE_NAME = "sessions.json";
    private const string SETTINGS_FILE_NAME = "settings.json";
    private const string ACTIVE_SESSIONS_FILE_NAME = "activesessions.json";

    private readonly string _appDataPath;
    private readonly string _sessionsFilePath;
    private readonly string _settingsFilePath;
    private readonly string _activeSessionsFilePath;

    public ConfigurationManager()
    {
        _appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), APP_NAME);
        _sessionsFilePath = Path.Combine(_appDataPath, SESSIONS_FILE_NAME);
        _settingsFilePath = Path.Combine(_appDataPath, SETTINGS_FILE_NAME);
        _activeSessionsFilePath = Path.Combine(_appDataPath, ACTIVE_SESSIONS_FILE_NAME);

        if (!Directory.Exists(_appDataPath))
        {
            Directory.CreateDirectory(_appDataPath);
        }
    }

    public string SessionsFilePath => _sessionsFilePath;
    public string SettingsFilePath => _settingsFilePath;
    public string ActiveSessionsFilePath => _activeSessionsFilePath;
    public string AppDataPath => _appDataPath;

    public T? LoadConfiguration<T>(string filePath) where T : class
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                NullValueHandling = NullValueHandling.Ignore
            };
            return JsonConvert.DeserializeObject<T>(json, settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load configuration from {filePath}: {ex.Message}");
            return null;
        }
    }

    public void SaveConfiguration<T>(T configuration, string filePath) where T : class
    {
        try
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.Indented
            };
            var json = JsonConvert.SerializeObject(configuration, settings);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to save configuration to {filePath}", ex);
        }
    }

    public T? LoadWindowState<T>(string filePath) where T : class
    {
        return LoadConfiguration<T>(filePath);
    }

    public void SaveWindowState<T>(T state, string filePath) where T : class
    {
        SaveConfiguration(state, filePath);
    }
}

