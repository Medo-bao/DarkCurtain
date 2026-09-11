using System.Text.Json;
using Microsoft.Win32;

namespace ScreenBlank;

internal sealed class Settings
{
    public bool MonitorPowerOff { get; set; } = false;
    public bool StartToTray { get; set; } = false;
    public bool CloseExits { get; set; } = false;
    public bool AutoStart { get; set; } = false;
    public int SettingsWindowX { get; set; } = -1;
    public int SettingsWindowY { get; set; } = -1;
}

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "暗黑天幕";

    public static void Apply(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            if (enabled)
            {
                string executable = Environment.ProcessPath
                    ?? Application.ExecutablePath;
                key.SetValue(ValueName, $"\"{executable}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Startup registration is best effort; the preference remains persisted.
        }
    }
}

internal static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ScreenBlank",
        "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                Settings? loaded = JsonSerializer.Deserialize<Settings>(json);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings fall back to defaults.
        }

        return new Settings();
    }

    public static void Save(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tempPath = $"{FilePath}.{Environment.ProcessId}.tmp";
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, FilePath, true);
        }
        catch
        {
            // Saving preferences is best effort.
        }
    }
}
