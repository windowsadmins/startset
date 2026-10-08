using CommunityToolkit.Mvvm.ComponentModel;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Gui;

namespace StartSet.App.ViewModels;

/// <summary>The kind of control a setting is edited with.</summary>
public enum SettingKind { Toggle, Number, Text, Choice, List }

/// <summary>
/// One setting on the Prefs tab: its effective value, where that value came from, and
/// whether this process may change it.
/// </summary>
public sealed class SettingItem
{
    public required PreferenceSetting Setting { get; init; }
    public required SettingDescription Description { get; init; }
    public required SettingKind Kind { get; init; }
    public required object? Value { get; init; }
    public required SettingSource Source { get; init; }
    public required bool CanEdit { get; init; }

    public string Name => Setting.Name;
    public bool IsManaged => Source == SettingSource.Policy;

    /// <summary>Where the shown value comes from, as the caption under the control says it.</summary>
    public string SourceText => Source switch
    {
        SettingSource.Policy => "Managed by policy",
        SettingSource.MachineSettings => "Machine setting",
        SettingSource.LegacyFile => "From Config.yaml",
        SettingSource.CommandLine => "Command line",
        _ => "Default"
    };

    public bool BoolValue => Value is true;
    public double NumberValue => Value is int i ? i : 0;
    public string TextValue => Value switch
    {
        List<string> list => string.Join(Environment.NewLine, list),
        null => string.Empty,
        _ => Value.ToString() ?? string.Empty
    };
}

/// <summary>
/// ViewModel for the Prefs tab. Every StartSet setting is shown with its effective value and
/// source. Settings live in HKLM, so the tab is read-only unless this process is elevated;
/// Unlock relaunches the app elevated on this tab. A setting policy manages shows the policy
/// value and stays locked. Elevated edits save to HKLM\SOFTWARE\StartSet\Settings.
/// </summary>
public partial class PrefsViewModel : ObservableObject
{
    public static readonly string[] LogLevels = ["", "Debug", "Information", "Warning", "Error"];

    private readonly Dictionary<string, System.Threading.Timer> _pendingSaves = new(StringComparer.OrdinalIgnoreCase);
    private PreferencesService _service = new();

    public List<SettingItem> Items { get; private set; } = [];

    // ── Elevation State ─────────────────────────────────────────

    /// <summary>True when this process can write HKLM (elevated administrator token).</summary>
    public bool IsElevated { get; } = PrefsElevation.IsProcessElevated();

    public bool IsReadOnly => !IsElevated;

    [ObservableProperty] private string _unlockError = "";

    public bool HasUnlockError => !string.IsNullOrEmpty(UnlockError);

    partial void OnUnlockErrorChanged(string value) => OnPropertyChanged(nameof(HasUnlockError));

    // ── Save Status ──────────────────────────────────────────────

    [ObservableProperty] private string _saveMessage = "";
    [ObservableProperty] private bool _saveFailed;

    public int ManagedCount => Items.Count(i => i.IsManaged);

    public string VersionDisplay
    {
        get
        {
            var version = typeof(PrefsViewModel).Assembly.GetName().Version;
            return version is null ? "Version unknown" : $"Version {version.Major}.{version.Minor:D2}.{version.Build:D2}.{version.Revision:D4}";
        }
    }

    // ── Load ─────────────────────────────────────────────────────

    public void Load()
    {
        _service = new PreferencesService();
        var preferences = _service.Load();

        Items = PreferenceResolver.Settings
            .Select(setting =>
            {
                var source = _service.Sources.TryGetValue(setting.Name, out var s) ? s : SettingSource.Default;
                return new SettingItem
                {
                    Setting = setting,
                    Description = SettingDescriptions.For(setting.Name),
                    Kind = KindOf(setting),
                    Value = setting.Property.GetValue(preferences),
                    Source = source,
                    CanEdit = PrefsElevation.CanEdit(IsElevated, source == SettingSource.Policy || setting.PolicyOnly),
                };
            })
            .ToList();

        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(ManagedCount));
    }

    private static SettingKind KindOf(PreferenceSetting setting)
    {
        var type = Nullable.GetUnderlyingType(setting.ValueType) ?? setting.ValueType;
        if (type == typeof(bool)) return SettingKind.Toggle;
        if (type == typeof(int)) return SettingKind.Number;
        if (type == typeof(List<string>)) return SettingKind.List;
        if (setting.Name == "LogLevel") return SettingKind.Choice;
        return SettingKind.Text;
    }

    // ── Save ─────────────────────────────────────────────────────

    /// <summary>
    /// Saves one edited value to machine settings after a short pause, so typing in a field
    /// writes once. Only an elevated process gets here; a policy-managed setting is never
    /// editable, and the service refuses one anyway.
    /// </summary>
    public void QueueSave(SettingItem item, object? value)
    {
        if (!item.CanEdit) return;

        if (_pendingSaves.Remove(item.Name, out var existing))
            existing.Dispose();

        _pendingSaves[item.Name] = new System.Threading.Timer(_ =>
        {
            try
            {
                _service.SaveMachineSetting(item.Name, Normalize(item, value));
                Report($"Saved {item.Description.Label}", failed: false);
            }
            catch (Exception ex)
            {
                Report($"Could not save {item.Description.Label}: {ex.Message}", failed: true);
            }
        }, null, 600, System.Threading.Timeout.Infinite);
    }

    /// <summary>Raised on the UI thread's behalf; the page marshals it.</summary>
    public event Action<string, bool>? SaveReported;

    private void Report(string message, bool failed) => SaveReported?.Invoke(message, failed);

    private static object Normalize(SettingItem item, object? value) => item.Kind switch
    {
        SettingKind.Toggle => value is true,
        SettingKind.Number => value is double d && !double.IsNaN(d) ? (int)Math.Round(d) : 0,
        SettingKind.List => (value as string ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList(),
        _ => value as string ?? string.Empty
    };

    // ── Unlock ───────────────────────────────────────────────────

    /// <summary>
    /// Relaunches this app elevated through UAC, opening on the Prefs tab. Returns true when the
    /// elevated instance started, so the caller can close this one. A cancelled UAC prompt
    /// returns false quietly and leaves the tab read-only.
    /// </summary>
    public bool TryRelaunchElevated()
    {
        UnlockError = "";
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                UnlockError = "Could not find the app's own executable to relaunch.";
                return false;
            }

            using var process = System.Diagnostics.Process.Start(PrefsElevation.BuildElevatedRelaunch(exe));
            return process is not null;
        }
        catch (Exception ex) when (PrefsElevation.IsElevationCancelled(ex))
        {
            return false;
        }
        catch (Exception ex)
        {
            UnlockError = $"Could not relaunch as administrator: {ex.Message}";
            return false;
        }
    }
}
