namespace ClaudeMon.Configuration;

using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeMon.Models;
using Microsoft.Win32;

public sealed class ConfigManager
{
    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "ClaudeMon";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _configPath;

    public AppSettings Settings { get; private set; } = new();

    public ConfigManager(string? configPath = null)
    {
        _configPath = configPath ?? GetDefaultConfigPath();
    }

    public void Load()
    {
        if (!File.Exists(_configPath))
        {
            Settings = new AppSettings();
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(Settings, JsonOptions);
        File.WriteAllText(_configPath, json);
    }

    public void Update(AppSettings newSettings)
    {
        Settings = newSettings;
        Save();
    }

    public static bool IsRunAtStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, false);
            return key?.GetValue(AppName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetRunAtStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, true);
            if (key is null) return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(AppName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch
        {
            // Silently fail if registry access denied
        }
    }

    private static string GetDefaultConfigPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeMon",
            "config.json");
}
