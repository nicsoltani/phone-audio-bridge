using System.Text.Json;

namespace PhoneAudioBridge;

/// <summary>User settings, stored in %APPDATA%\PhoneAudioBridge\settings.json.</summary>
sealed class Settings
{
    public bool SpeakerEnabled { get; set; } = true;
    public bool MicEnabled { get; set; } = true;
    /// <summary>Playback device whose sound is sent to the phone. Null = Windows default output.</summary>
    public string? SourceDeviceId { get; set; }
    /// <summary>Playback side of the virtual cable the phone mic is played into. Null = find VB-CABLE.</summary>
    public string? MicCableDeviceId { get; set; }
    /// <summary>Full path of adb.exe. Null = search next to the app, PATH and common locations.</summary>
    public string? AdbPath { get; set; }
    public bool AutoLaunchPhoneApp { get; set; } = true;

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoneAudioBridge");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) { Log.Write("Settings could not be read: " + ex.Message); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("Settings could not be saved: " + ex.Message); }
    }
}

static class Log
{
    static readonly object Gate = new();
    public static string FilePath => Path.Combine(Settings.Dir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
