using System.Collections.ObjectModel;
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

    /// <summary>
    /// Where the shown value comes from, as the caption under the control says it. A value set
    /// by policy has no caption: its control is grayed out, and one line at the top of the tab
    /// says what gray means.
    /// </summary>
    public string SourceText => Source switch
    {
        SettingSource.Policy => "",
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
/// source. A setting policy sets shows the policy value and cannot be edited; every other one
/// can, in the normal app. Edits are held until Save, which writes them to
/// HKLM\SOFTWARE\StartSet\Settings: directly when this process is elevated, otherwise through
/// an elevated, windowless run of this app that UAC asks the user to approve.
/// </summary>
public partial class PrefsViewModel : ObservableObject
{
    private readonly Dictionary<string, object> _pending = new(StringComparer.OrdinalIgnoreCase);
    private PreferencesService _service = new();

    public List<SettingItem> Items { get; private set; } = [];

    /// <summary>True when this process can write HKLM itself, so Save needs no UAC prompt.</summary>
    public bool IsElevated { get; } = PrefsElevation.IsProcessElevated();

    public int ManagedCount => Items.Count(i => i.IsManaged);

    /// <summary>True when policy sets at least one value, which shows the policy line at the top.</summary>
    public bool HasPolicySettings => ManagedCount > 0;

    // ── Services ─────────────────────────────────────────────────

    /// <summary>The installer's services and scheduled tasks, for the read-only Services card.</summary>
    public ObservableCollection<FootprintStatus> Services { get; } = [];

    /// <summary>Queries the services off the UI thread and replaces the card's rows.</summary>
    public async Task RefreshServicesAsync()
    {
        var rows = await Task.Run(InstalledServices.Query);
        Services.Clear();
        foreach (var row in rows)
            Services.Add(row);
    }

    public string VersionDisplay
    {
        get
        {
            var version = typeof(PrefsViewModel).Assembly.GetName().Version;
            return version is null ? "Version unknown" : $"Version {version.Major}.{version.Minor:D2}.{version.Build:D2}.{version.Revision:D4}";
        }
    }

    // ── Load ─────────────────────────────────────────────────────

    /// <summary>Raised after the settings are read again, so the page rebuilds its cards.</summary>
    public event Action? Reloaded;

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
                    CanEdit = PrefsElevation.CanEdit(source == SettingSource.Policy || setting.PolicyOnly),
                };
            })
            .ToList();

        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(ManagedCount));
        OnPropertyChanged(nameof(HasPolicySettings));
        Reloaded?.Invoke();
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

    // ── Pending edits ────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    [NotifyPropertyChangedFor(nameof(PendingSummary))]
    private int _pendingCount;

    [ObservableProperty] private bool _isSaving;

    public bool HasPendingChanges => PendingCount > 0;

    public string PendingSummary => PendingCount == 1 ? "1 unsaved change" : $"{PendingCount} unsaved changes";

    /// <summary>
    /// Holds an edited value until Save. A value that breaks its setting's rules is reported and
    /// not held; one changed back to what is saved is no longer pending.
    /// </summary>
    public void SetPending(SettingItem item, object? value)
    {
        if (!item.CanEdit) return;

        try
        {
            var normalized = SettingsPayload.Normalize(item.Setting, value);
            if (SameValue(item, normalized))
                _pending.Remove(item.Name);
            else
                _pending[item.Name] = normalized;
        }
        catch (ArgumentException)
        {
            _pending.Remove(item.Name);
            Report($"Not saved: {item.Description.Label} is not a valid value.", failed: true);
        }
        PendingCount = _pending.Count;
    }

    private static bool SameValue(SettingItem item, object normalized)
    {
        object saved;
        try { saved = SettingsPayload.Normalize(item.Setting, item.Value); }
        catch (ArgumentException) { return false; }

        return saved is List<string> a && normalized is List<string> b
            ? a.SequenceEqual(b)
            : Equals(saved, normalized);
    }

    /// <summary>Drops every unsaved edit and shows the saved values again.</summary>
    public void Discard()
    {
        _pending.Clear();
        PendingCount = 0;
        Load();
    }

    // ── Save ─────────────────────────────────────────────────────

    /// <summary>
    /// Writes the pending edits to machine settings. An elevated process writes them itself;
    /// otherwise this app's executable is started elevated and sent the edits over a pipe, and
    /// UAC asks for approval. A declined prompt keeps the edits pending.
    /// </summary>
    public async Task SaveAsync()
    {
        if (_pending.Count == 0 || IsSaving) return;
        IsSaving = true;
        try
        {
            var values = new Dictionary<string, object>(_pending, StringComparer.OrdinalIgnoreCase);
            var failure = IsElevated ? await SaveDirectAsync(values) : await SaveElevatedAsync(values);
            if (failure is not null)
            {
                Report(failure, failed: true);
                return;
            }

            _pending.Clear();
            PendingCount = 0;
            Load();
            Report(values.Count == 1 ? "Saved 1 setting" : $"Saved {values.Count} settings", failed: false);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private async Task<string?> SaveDirectAsync(Dictionary<string, object> values)
    {
        try
        {
            await Task.Run(() =>
            {
                var service = new PreferencesService();
                service.Load();
                SettingsPayload.Apply(values, service);
            });
            return null;
        }
        catch (Exception ex)
        {
            return $"Not saved: {ex.Message}";
        }
    }

    private static async Task<string?> SaveElevatedAsync(Dictionary<string, object> values)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return "Not saved: could not find the app's own executable.";

        try
        {
            // The edits travel over a pipe only this user, Administrators and SYSTEM may open;
            // nothing is written to disk.
            var pipeName = SettingsPayload.NewPipeName();
            using var pipe = SettingsPayload.CreatePipe(pipeName);
            using var process = System.Diagnostics.Process.Start(PrefsElevation.BuildElevatedApply(exe, pipeName));
            if (process is null)
                return "Not saved: the settings writer did not start.";

            // A writer that exits before it connects must not leave Save waiting out the timeout.
            var send = SettingsPayload.SendAsync(pipe, values, SettingsPayload.ConnectTimeout);
            var exited = process.WaitForExitAsync();
            if (await Task.WhenAny(send, exited) == send)
                await send;

            await exited;
            return process.ExitCode == SettingsPayload.ExitSuccess
                ? null
                : $"Not saved: {SettingsPayload.ExitCodeText(process.ExitCode)}.";
        }
        catch (Exception ex) when (PrefsElevation.IsElevationCancelled(ex))
        {
            return "Not saved: administrator approval was declined.";
        }
        catch (Exception ex)
        {
            return $"Not saved: {ex.Message}";
        }
    }

    /// <summary>Raised with a status line for the page to show.</summary>
    public event Action<string, bool>? SaveReported;

    private void Report(string message, bool failed) => SaveReported?.Invoke(message, failed);
}
