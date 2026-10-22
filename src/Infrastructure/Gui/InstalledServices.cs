using System.Management;

namespace StartSet.Infrastructure.Gui;

/// <summary>A Windows service or scheduled task the StartSet installer creates.</summary>
public sealed record FootprintEntry(string Name, string FriendlyName, string Kind, string Description);

/// <summary>One label and value in an expanded Services row.</summary>
public sealed record FootprintDetail(string Label, string Value);

/// <summary>One row of the Prefs tab's Services card.</summary>
public sealed record FootprintStatus(
    string FriendlyName,
    string Name,
    string Kind,
    string Description,
    string State,
    string Summary,
    IReadOnlyList<FootprintDetail> Details);

/// <summary>
/// The services and scheduled tasks StartSet installs, and their state for the Prefs tab's
/// read-only Services card.
/// </summary>
/// <remarks>
/// The list is the installer's footprint, kept by hand: the package's postinstall.ps1 and the
/// MSI both create the one service, StartSet, set to start automatically (not delayed). Neither
/// creates a scheduled task. State comes from WMI's Win32_Service, which a standard user may
/// read, so the card needs no elevation; a query that fails shows "Unknown". The detail labels
/// follow the fields ReportMate's Windows System module reports for a service.
/// </remarks>
public static class InstalledServices
{
    public const string ServiceKind = "Service";

    public const string NotInstalled = "Not installed";
    public const string Unknown = "Unknown";

    public const string NameLabel = "Name";
    public const string StartTypeLabel = "Start type";
    public const string StatusLabel = "Status";
    public const string AccountLabel = "Account";
    public const string PathLabel = "Path and arguments";
    public const string TriggersLabel = "Triggers";
    public const string LastResultLabel = "Last result";

    public static IReadOnlyList<FootprintEntry> Footprint { get; } =
    [
        new("StartSet", "StartSet Service", ServiceKind,
            "Runs boot payloads at start-up, login payloads at each sign-in, and on-demand payloads when asked."),
    ];

    /// <summary>Queries every footprint entry. Blocking; call it off the UI thread.</summary>
    public static IReadOnlyList<FootprintStatus> Query() => Footprint.Select(QueryService).ToList();

    private static FootprintStatus QueryService(FootprintEntry entry)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT State, StartMode, DelayedAutoStart, ExitCode, StartName, PathName FROM Win32_Service WHERE Name = '{entry.Name}'");
            using var results = searcher.Get();
            var service = results.Cast<ManagementObject>().FirstOrDefault();
            if (service is null)
                return Row(entry, NotInstalled, ServiceSummary(null, false, null), [new(StatusLabel, NotInstalled)]);

            var state = service["State"] as string;
            var startMode = service["StartMode"] as string;
            var delayed = service["DelayedAutoStart"] is true;
            var account = service["StartName"] as string;
            var status = ServiceStateText(state, startMode);
            var details = ServiceDetails(
                entry.Name,
                status,
                startMode,
                delayed,
                state,
                exitCode: service["ExitCode"] is uint code ? (int)code : null,
                account,
                path: service["PathName"] as string);
            return Row(entry, status, ServiceSummary(startMode, delayed, account), details);
        }
        catch (Exception)
        {
            return Row(entry, Unknown, ServiceSummary(null, false, null), [new(StatusLabel, Unknown)]);
        }
    }

    private static FootprintStatus Row(FootprintEntry entry, string state, string summary, IReadOnlyList<FootprintDetail> details) =>
        new(entry.FriendlyName, entry.Name, entry.Kind, entry.Description, state, summary, details);

    // ── Plain words ─────────────────────────────────────────────

    /// <summary>
    /// The collapsed row's one summary line, e.g. "Service · Automatic · LocalSystem". A part
    /// that cannot be read is left out.
    /// </summary>
    public static string ServiceSummary(string? startMode, bool delayed, string? account) =>
        string.Join(" · ", new[] { ServiceKind, StartTypeText(startMode, delayed), account }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>
    /// A service's expanded rows, in the card's fixed order. A row whose value is missing is
    /// left out rather than shown empty.
    /// </summary>
    public static IReadOnlyList<FootprintDetail> ServiceDetails(
        string name, string status, string? startMode, bool delayed, string? state, int? exitCode, string? account, string? path)
    {
        var rows = new List<FootprintDetail>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                rows.Add(new(label, value));
        }

        Add(NameLabel, name);
        Add(StartTypeLabel, StartTypeText(startMode, delayed));
        Add(StatusLabel, status);
        Add(AccountLabel, account);
        Add(PathLabel, path);
        Add(TriggersLabel, ServiceTriggerText(startMode, delayed));
        Add(LastResultLabel, ServiceLastResultText(state, exitCode));
        return rows;
    }

    /// <summary>Win32_Service.StartMode ("Auto", "Manual", "Disabled", "Boot", "System") as a start type.</summary>
    public static string? StartTypeText(string? startMode, bool delayed) => startMode switch
    {
        "Auto" when delayed => "Automatic (delayed)",
        "Auto" or "Boot" or "System" => "Automatic",
        "Manual" => "Manual",
        "Disabled" => "Disabled",
        _ => null
    };

    /// <summary>What starts the service, in words, from its start type.</summary>
    public static string? ServiceTriggerText(string? startMode, bool delayed) => startMode switch
    {
        "Auto" when delayed => "Shortly after boot (delayed start)",
        "Auto" or "Boot" or "System" => "At boot",
        "Manual" => "When started by a user or another program",
        "Disabled" => "None: the service is disabled",
        _ => null
    };

    /// <summary>
    /// Win32_Service.State in words. A disabled service reads "Disabled" whatever it is doing,
    /// since that is what stops it running.
    /// </summary>
    public static string ServiceStateText(string? state, string? startMode)
    {
        if (startMode == "Disabled") return "Disabled";
        return state switch
        {
            "Running" => "Running",
            "Stopped" => "Stopped",
            "Start Pending" or "Continue Pending" => "Starting",
            "Stop Pending" or "Pause Pending" => "Stopping",
            "Paused" => "Paused",
            _ => Unknown
        };
    }

    /// <summary>
    /// The service's Win32 exit code, which only means something once it has stopped. While it
    /// runs there is no last result, and the row is left out.
    /// </summary>
    public static string? ServiceLastResultText(string? state, int? exitCode) =>
        state == "Stopped" && exitCode is { } code ? $"Exit code {code}" : null;
}
