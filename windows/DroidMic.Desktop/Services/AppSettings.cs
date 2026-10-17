using System;
using System.IO;
using System.Text.Json;
using DroidMic.AudioEngine;

namespace DroidMic.Desktop.Services;

/// <summary>User preferences, persisted as JSON in %AppData%\DroidMic\settings.json.</summary>
public sealed class AppSettings
{
    public bool StartListeningOnLaunch { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool MonitorOnSpeakers { get; set; }
    /// <summary>Render endpoint for the monitor output; null = Windows default.</summary>
    public string? MonitorDeviceId { get; set; }
    public LatencyPreset LatencyPreset { get; set; } = LatencyPreset.Balanced;
    public bool EnableDiscovery { get; set; } = true;
    public int ControlPort { get; set; } = 48110;
    public int AudioPort { get; set; } = 48111;
    public string? ServerName { get; set; }

    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DroidMic");
    private static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Corrupt settings: fall back to defaults rather than failing to start.
        }
        return new AppSettings();
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, true);
    }
}
