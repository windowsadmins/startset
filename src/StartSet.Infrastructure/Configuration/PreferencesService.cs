using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using StartSet.Core.Constants;
using StartSet.Core.Models;
using StartSet.Infrastructure.Logging;

namespace StartSet.Infrastructure.Configuration;

/// <summary>
/// Service for loading and managing StartSet YAML preferences.
/// </summary>
public class PreferencesService
{
    private static readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private StartSetPreferences _preferences = StartSetPreferences.Default;
    private string _preferencesPath = Paths.PreferencesFile;
    private FileSystemWatcher? _watcher;

    /// <summary>
    /// Event raised when preferences are reloaded.
    /// </summary>
    public event EventHandler<StartSetPreferences>? PreferencesChanged;

    /// <summary>
    /// Gets the current preferences.
    /// </summary>
    public StartSetPreferences Preferences => _preferences;

    /// <summary>
    /// Loads preferences from the default or specified path.
    /// </summary>
    /// <param name="path">Optional custom path to preferences file</param>
    /// <returns>Loaded preferences (or defaults if file doesn't exist)</returns>
    public StartSetPreferences Load(string? path = null)
    {
        _preferencesPath = path ?? Paths.PreferencesFile;

        if (!File.Exists(_preferencesPath))
        {
            StartSetLogger.Debug("Preferences file not found at {Path}, using defaults", _preferencesPath);
            _preferences = StartSetPreferences.Default;
            return _preferences;
        }

        try
        {
            var yaml = File.ReadAllText(_preferencesPath);
            _preferences = _deserializer.Deserialize<StartSetPreferences>(yaml) ?? StartSetPreferences.Default;
            StartSetLogger.Information("Loaded preferences from {Path}", _preferencesPath);
        }
        catch (Exception ex)
        {
            StartSetLogger.Warning("Failed to load preferences from {Path}, using defaults: {Error}", _preferencesPath, ex.Message);
            _preferences = StartSetPreferences.Default;
        }

        return _preferences;
    }

    /// <summary>
    /// Saves preferences to the specified path.
    /// </summary>
    /// <param name="preferences">Preferences to save</param>
    /// <param name="path">Optional custom path</param>
    public void Save(StartSetPreferences preferences, string? path = null)
    {
        var targetPath = path ?? _preferencesPath;

        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var yaml = _serializer.Serialize(preferences);
            File.WriteAllText(targetPath, yaml);
            _preferences = preferences;
            StartSetLogger.Information("Saved preferences to {Path}", targetPath);
        }
        catch (Exception ex)
        {
            StartSetLogger.Error(ex, "Failed to save preferences to {Path}", targetPath);
            throw;
        }
    }

    /// <summary>
    /// Creates default preferences file if it doesn't exist.
    /// </summary>
    public void EnsureDefaultPreferences()
    {
        if (!File.Exists(_preferencesPath))
        {
            Save(StartSetPreferences.Default);
        }
    }

    /// <summary>
    /// Enables watching for preference file changes.
    /// </summary>
    public void EnableFileWatcher()
    {
        DisableFileWatcher();

        var directory = Path.GetDirectoryName(_preferencesPath);
        var filename = Path.GetFileName(_preferencesPath);

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        _watcher = new FileSystemWatcher(directory, filename)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Changed += OnPreferencesFileChanged;
        _watcher.EnableRaisingEvents = true;
        StartSetLogger.Debug("Enabled preferences file watcher for {Path}", _preferencesPath);
    }

    /// <summary>
    /// Disables the file watcher.
    /// </summary>
    public void DisableFileWatcher()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnPreferencesFileChanged;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void OnPreferencesFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            // Debounce - wait for file to be fully written
            Thread.Sleep(100);
            Load();
            PreferencesChanged?.Invoke(this, _preferences);
        }
        catch (Exception ex)
        {
            StartSetLogger.Warning(ex, "Error reloading preferences after file change");
        }
    }
}
