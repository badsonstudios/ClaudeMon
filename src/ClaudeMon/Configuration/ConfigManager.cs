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
            Settings = Migrate(JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings());
        }
        catch
        {
            Settings = new AppSettings();
        }
    }

    /// <summary>
    /// Upgrades settings written by older versions. Currently: the 0.10.x "Also show 7-day
    /// usage" toggle becomes the weekly display toggle (<c>true</c> → weekly on; <c>false</c> or
    /// absent → unchanged defaults). The legacy field is cleared either way so the next save
    /// drops the old key.
    /// </summary>
    private static AppSettings Migrate(AppSettings settings)
    {
        var taskbar = settings.TaskbarDisplay;
        if (taskbar.LegacyShowSevenDay is null)
            return settings;

        return settings with
        {
            TaskbarDisplay = taskbar with
            {
                ShowWeeklyUsage = taskbar.ShowWeeklyUsage || taskbar.LegacyShowSevenDay == true,
                LegacyShowSevenDay = null,
            },
        };
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Write atomically and best-effort: serialize to a temp file in the same directory, then
        // swap it into place so a crash mid-write can't truncate config.json and make Load()
        // silently reset to defaults. File.Replace is a true atomic, ACL-preserving swap (used once
        // the config exists); on first run there's nothing to replace, so move it into place. A
        // transient lock (AV scanner, another reader) must not crash the app or orphan the temp —
        // the in-memory Settings still serve this session and the next Save retries. Mirrors
        // CredentialReader.WriteBack (which is where the credentials file gets the same treatment).
        var json = JsonSerializer.Serialize(Settings, JsonOptions);
        var tempPath = _configPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(_configPath))
                File.Replace(tempPath, _configPath, destinationBackupFileName: null);
            else
                File.Move(tempPath, _configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemp(tempPath);
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; leaving a temp file is not worth surfacing.
        }
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
