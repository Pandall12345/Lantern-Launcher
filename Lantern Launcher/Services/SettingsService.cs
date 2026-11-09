using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanternLauncher.Services;

/// <summary>
/// Strongly-typed application settings, persisted as JSON.
/// Implements INotifyPropertyChanged so the Settings page's TwoWay bindings can
/// auto-save: ViewModel subscribes once, any bound edit persists immediately.
/// </summary>
public class AppSettings : INotifyPropertyChanged
{
    private string _windowMode = "Windowed"; // Fullscreen | Windowed | Borderless
    public string WindowMode
    {
        get => _windowMode;
        set { if (_windowMode != value) { _windowMode = value; OnPropertyChanged(); } }
    }

    private bool _autoUpdate = true;
    public bool AutoUpdate
    {
        get => _autoUpdate;
        set { if (_autoUpdate != value) { _autoUpdate = value; OnPropertyChanged(); } }
    }

    private string _logLevel = "Warning";    // Trace|Debug|Info|Warning|Error
    public string LogLevel
    {
        get => _logLevel;
        set { if (_logLevel != value) { _logLevel = value; OnPropertyChanged(); } }
    }

    private string _lastLibrary = "Lantern";
    public string LastLibrary
    {
        get => _lastLibrary;
        set { if (_lastLibrary != value) { _lastLibrary = value; OnPropertyChanged(); } }
    }

    private string _downloadFolder = "";
    /// <summary>Last folder used for downloads. Empty means "use the profile's Downloads".</summary>
    public string DownloadFolder
    {
        get => _downloadFolder;
        set { if (_downloadFolder != value) { _downloadFolder = value; OnPropertyChanged(); } }
    }

    private bool _reduceMotion;
    /// <summary>Reduced motion: drops every slide and scale transition, keeping only fades.</summary>
    public bool ReduceMotion
    {
        get => _reduceMotion;
        set { if (_reduceMotion != value) { _reduceMotion = value; OnPropertyChanged(); } }
    }


    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Loads/saves <see cref="AppSettings"/> as JSON under %APPDATA%\LanternLauncher\.
/// Never throws: corrupt or missing file → defaults. Atomic write (temp+rename).
/// </summary>
public class SettingsService
{
    public static SettingsService Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string SettingsPath { get; }

    private AppSettings? _cached;

    public SettingsService()
    {
        SettingsPath = AppInfo.SettingsFile("settings.json");
    }

    /// <summary>Load settings once; subsequent calls return the cached instance.
    /// Pass forceReload: true to re-read from disk.</summary>
    public AppSettings Load(bool forceReload = false)
    {
        if (_cached is { } c && !forceReload) return c;

        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _cached = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
            else
            {
                _cached = new AppSettings();
            }
        }
        catch (Exception) // corrupt JSON / IO error → defaults, never crash the app
        {
            _cached = new AppSettings();
        }
        return _cached;
    }

    /// <summary>Atomic save: write to temp file, then replace. Corrupt-half-written
    /// files are impossible. IO failures are swallowed so a settings write can
    /// never crash a UI click handler.</summary>
    public void Save(AppSettings settings)
    {
        _cached = settings;
        var tmp = SettingsPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch (IOException)
        {
            TryDeleteTemp(tmp);
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteTemp(tmp);
        }
    }

    private static void TryDeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
