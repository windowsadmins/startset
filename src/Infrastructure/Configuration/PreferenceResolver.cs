using System.Globalization;
using System.Reflection;
using YamlDotNet.Serialization;
using StartSet.Core.Models;

namespace StartSet.Infrastructure.Configuration;

/// <summary>Where the effective value of a setting came from, lowest precedence first.</summary>
public enum SettingSource
{
    Default,
    LegacyFile,
    MachineSettings,
    Policy,
    CommandLine
}

/// <summary>
/// One setting in <see cref="StartSetPreferences"/>. Its registry value name is the property
/// name (WaitForNetwork); the Config.yaml name (wait_for_network) is accepted as an alias.
/// </summary>
public sealed record PreferenceSetting(string Name, string YamlName, PropertyInfo Property)
{
    public Type ValueType => Property.PropertyType;
}

/// <summary>The effective preferences, and which source supplied each one.</summary>
public sealed class PreferenceResolution
{
    public required StartSetPreferences Preferences { get; init; }
    public required IReadOnlyDictionary<string, SettingSource> Sources { get; init; }

    /// <summary>Values that were present but could not be used, one line each, for the log.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    public SettingSource SourceOf(string name) =>
        Sources.TryGetValue(name, out var source) ? source : SettingSource.Default;
}

/// <summary>
/// Applies the suite precedence to StartSet's settings. Highest first:
///   1. a one-off command-line flag for this run
///   2. policy -- HKLM\SOFTWARE\Policies\StartSet
///   3. machine settings -- HKLM\SOFTWARE\StartSet\Settings
///   4. the legacy C:\ProgramData\ManagedState\Config.yaml
///   5. the built-in default
/// Environment variables are not a source.
/// </summary>
/// <remarks>
/// The settings are discovered from <see cref="StartSetPreferences"/> by reflection, so every
/// setting the tool reads can be set at every level -- a new property is policy-settable the
/// moment it exists, with nothing else to remember.
/// </remarks>
public static class PreferenceResolver
{
    public static IReadOnlyList<PreferenceSetting> Settings { get; } = typeof(StartSetPreferences)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite)
        .Select(p => new PreferenceSetting(
            p.Name,
            p.GetCustomAttribute<YamlMemberAttribute>()?.Alias ?? p.Name,
            p))
        .ToList();

    /// <summary>The setting called <paramref name="name"/>, by registry or Config.yaml name.</summary>
    public static PreferenceSetting? Find(string name) => Settings.FirstOrDefault(s =>
        string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(s.YamlName, name, StringComparison.OrdinalIgnoreCase));

    public static PreferenceResolution Resolve(
        IReadOnlyDictionary<string, object?>? legacyFile,
        ISettingsStore? machine,
        ISettingsStore? policy,
        IReadOnlyDictionary<string, object?>? commandLine = null)
    {
        var preferences = StartSetPreferences.Default;
        var sources = new Dictionary<string, SettingSource>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();

        foreach (var setting in Settings)
        {
            sources[setting.Name] = SettingSource.Default;

            Apply(setting, FromDictionary(legacyFile, setting), SettingSource.LegacyFile);
            Apply(setting, FromStore(machine, setting), SettingSource.MachineSettings);
            Apply(setting, FromStore(policy, setting), SettingSource.Policy);
            Apply(setting, FromDictionary(commandLine, setting), SettingSource.CommandLine);
        }

        return new PreferenceResolution { Preferences = preferences, Sources = sources, Notes = notes };

        void Apply(PreferenceSetting setting, object? raw, SettingSource source)
        {
            if (raw is null) return;
            if (TryConvert(raw, setting.ValueType, out var value))
            {
                setting.Property.SetValue(preferences, value);
                sources[setting.Name] = source;
            }
            else
            {
                notes.Add($"{setting.Name} from {Describe(source)} is ignored: '{raw}' is not a valid {Describe(setting.ValueType)}");
            }
        }
    }

    /// <summary>Human name for a source, as the log and the GUI show it.</summary>
    public static string Describe(SettingSource source) => source switch
    {
        SettingSource.CommandLine => "the command line",
        SettingSource.Policy => "policy",
        SettingSource.MachineSettings => "machine settings",
        SettingSource.LegacyFile => "Config.yaml",
        _ => "default"
    };

    private static object? FromStore(ISettingsStore? store, PreferenceSetting setting) =>
        store is null ? null : store.GetValue(setting.Name) ?? store.GetValue(setting.YamlName);

    private static object? FromDictionary(IReadOnlyDictionary<string, object?>? values, PreferenceSetting setting)
    {
        if (values is null) return null;
        foreach (var (key, value) in values)
        {
            if (string.Equals(key, setting.YamlName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, setting.Name, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }

    /// <summary>
    /// Converts a registry or YAML value to a setting's type: DWORD or "true"/"1" for bools,
    /// DWORD or digits for ints, and REG_MULTI_SZ, a YAML list, or a comma/semicolon/newline
    /// separated string for lists.
    /// </summary>
    public static bool TryConvert(object raw, Type target, out object? value)
    {
        value = null;
        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        if (underlying == typeof(bool))
        {
            switch (raw)
            {
                case bool b: value = b; return true;
                case int i: value = i != 0; return true;
                case long l: value = l != 0; return true;
                case string s when bool.TryParse(s.Trim(), out var parsed): value = parsed; return true;
                case string s when int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n): value = n != 0; return true;
                default: return false;
            }
        }

        if (underlying == typeof(int))
        {
            switch (raw)
            {
                case int i: value = i; return true;
                case long l when l is >= int.MinValue and <= int.MaxValue: value = (int)l; return true;
                case string s when int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n): value = n; return true;
                default: return false;
            }
        }

        if (underlying == typeof(string))
        {
            if (raw is string s) { value = s.Length == 0 ? null : s; return true; }
            if (raw is int or long or bool) { value = Convert.ToString(raw, CultureInfo.InvariantCulture); return true; }
            return false;
        }

        if (underlying == typeof(List<string>))
        {
            IEnumerable<string>? items = raw switch
            {
                string[] array => array,
                string s => s.Split([',', ';', '\n', '\r'], StringSplitOptions.None),
                IEnumerable<object> list => list.Select(o => Convert.ToString(o, CultureInfo.InvariantCulture) ?? string.Empty),
                _ => null
            };
            if (items is null) return false;
            value = items.Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
            return true;
        }

        return false;
    }

    private static string Describe(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(bool)) return "true/false or 0/1";
        if (underlying == typeof(int)) return "whole number";
        if (underlying == typeof(List<string>)) return "list";
        return "text value";
    }
}
