using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using StartSet.Core.Constants;
using StartSet.Core.Models;
using StartSet.Infrastructure.Logging;
using StartSet.Infrastructure.Security;

namespace StartSet.Infrastructure.Configuration;

/// <summary>
/// Loads StartSet's settings from policy, machine settings, the legacy Config.yaml and the
/// built-in defaults, in the order <see cref="PreferenceResolver"/> describes.
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

    private readonly ISettingsStore? _policy;
    private readonly ISettingsStore? _machine;
    private readonly Func<string, TrustResult> _fileTrust;
    private IReadOnlyDictionary<string, object?>? _commandLine;

    private StartSetPreferences _preferences = StartSetPreferences.Default;
    private IReadOnlyDictionary<string, SettingSource> _sources = new Dictionary<string, SettingSource>();
    private string _preferencesPath = Paths.PreferencesFile;
    private FileSystemWatcher? _watcher;

    /// <summary>Reads the real policy and machine settings keys and checks Config.yaml's ACL.</summary>
    public PreferencesService()
        : this(RegistrySettingsStore.Policy(), RegistrySettingsStore.Machine(), FileTrust.CheckFile)
    {
    }

    /// <param name="policy">Policy values; null for none.</param>
    /// <param name="machine">Machine settings; null for none.</param>
    /// <param name="fileTrust">Decides whether Config.yaml may be read.</param>
    public PreferencesService(ISettingsStore? policy, ISettingsStore? machine, Func<string, TrustResult> fileTrust)
    {
        _policy = policy;
        _machine = machine;
        _fileTrust = fileTrust;
    }

    /// <summary>
    /// Event raised when preferences are reloaded.
    /// </summary>
    public event EventHandler<StartSetPreferences>? PreferencesChanged;

    /// <summary>
    /// Gets the current preferences.
    /// </summary>
    public StartSetPreferences Preferences => _preferences;

    /// <summary>Which source supplied each setting's effective value, by registry name.</summary>
    public IReadOnlyDictionary<string, SettingSource> Sources => _sources;

    /// <summary>True when policy sets <paramref name="name"/>, so nothing below it can change it.</summary>
    public bool IsManaged(string name)
    {
        var setting = PreferenceResolver.Find(name);
        return setting is not null && _policy is not null &&
            (_policy.GetValue(setting.Name) ?? _policy.GetValue(setting.YamlName)) is not null;
    }

    /// <summary>
    /// One-off values for this run only, by setting name, applied above policy. Takes
    /// effect at the next <see cref="Load"/>.
    /// </summary>
    public void SetCommandLineOverrides(IReadOnlyDictionary<string, object?>? overrides)
    {
        _commandLine = overrides;
    }

    /// <summary>
    /// Loads the effective preferences.
    /// </summary>
    /// <param name="path">Optional custom path to the legacy Config.yaml</param>
    /// <returns>The effective preferences</returns>
    public StartSetPreferences Load(string? path = null)
    {
        _preferencesPath = path ?? Paths.PreferencesFile;

        var resolution = PreferenceResolver.Resolve(ReadLegacyFile(_preferencesPath), _machine, _policy, _commandLine);
        foreach (var note in resolution.Notes)
            StartSetLogger.Warning("{Note}", note);

        _preferences = resolution.Preferences;
        _sources = resolution.Sources;
        return _preferences;
    }

    /// <summary>Loads again from the same Config.yaml path, picking up policy and settings changes.</summary>
    public StartSetPreferences Reload() => Load(_preferencesPath);

    /// <summary>
    /// The legacy file's values, or null when it is missing, unreadable, or could have been
    /// written by a non-administrator -- the service runs as SYSTEM and acts on these, so a
    /// file anyone could edit is not a setting.
    /// </summary>
    private IReadOnlyDictionary<string, object?>? ReadLegacyFile(string path)
    {
        if (!File.Exists(path))
        {
            StartSetLogger.Debug("Preferences file not found at {Path}", path);
            return null;
        }

        var trust = _fileTrust(path);
        if (!trust.IsTrusted)
        {
            StartSetLogger.Warning("Ignoring {Path}: {Reason}. Only a file that just Administrators and SYSTEM can write is read", path, trust.Reason ?? "it is not trusted");
            return null;
        }

        try
        {
            var yaml = File.ReadAllText(path);
            var values = _deserializer.Deserialize<Dictionary<string, object?>>(yaml);
            StartSetLogger.Information("Loaded preferences from {Path}", path);
            return values;
        }
        catch (Exception ex)
        {
            StartSetLogger.Warning("Failed to load preferences from {Path}, ignoring it: {Error}", path, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Writes one setting to machine settings (HKLM\SOFTWARE\StartSet\Settings). Refuses a
    /// setting policy manages, since the write could never take effect. Needs an elevated process.
    /// </summary>
    public void SaveMachineSetting(string name, object value)
    {
        var setting = PreferenceResolver.Find(name)
            ?? throw new ArgumentException($"Unknown setting: {name}", nameof(name));
        if (IsManaged(setting.Name))
            throw new InvalidOperationException($"{setting.Name} is set by policy and cannot be changed here");
        if (_machine is null)
            throw new InvalidOperationException("No machine settings store is available");

        _machine.SetValue(setting.Name, value);
        StartSetLogger.Information("Saved {Setting} to machine settings", setting.Name);
        Reload();
    }

    /// <summary>
    /// Saves preferences to a legacy YAML file. The service no longer writes Config.yaml;
    /// use <see cref="SaveMachineSetting"/> to change a setting.
    /// </summary>
    /// <param name="preferences">Preferences to save</param>
    /// <param name="path">Optional custom path</param>
    public void Save(StartSetPreferences preferences, string? path = null)
    {
        var targetPath = path ?? _preferencesPath;

        try
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var yaml = _serializer.Serialize(preferences);
            File.WriteAllText(targetPath, yaml);
            StartSetLogger.Information("Saved preferences to {Path}", targetPath);
        }
        catch (Exception ex)
        {
            StartSetLogger.Error(ex, "Failed to save preferences to {Path}", targetPath);
            throw;
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
            Reload();
            PreferencesChanged?.Invoke(this, _preferences);
        }
        catch (Exception ex)
        {
            StartSetLogger.Warning(ex, "Error reloading preferences after file change");
        }
    }
}
