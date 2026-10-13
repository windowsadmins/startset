using System.Management;

namespace StartSet.Infrastructure.Gui;

/// <summary>A Windows service or scheduled task the StartSet installer creates.</summary>
public sealed record FootprintEntry(string Name, string FriendlyName, string Kind, string Description);

/// <summary>One row of the Prefs tab's Services card.</summary>
public sealed record FootprintStatus(
    string FriendlyName,
    string Name,
    string Kind,
    string Description,
    string State,
    string LastResult,
    string Trigger)
{
    public string NameAndKind => $"{Name} · {Kind}";
}

/// <summary>
/// The services and scheduled tasks StartSet installs, and their state for the Prefs tab's
/// read-only Services card.
/// </summary>
/// <remarks>
/// The list is the installer's footprint, kept by hand: the package's postinstall.ps1 and the
/// MSI both create the one service, StartSet, set to start automatically (not delayed). Neither
/// creates a scheduled task. State comes from WMI's Win32_Service, which a standard user may
/// read, so the card needs no elevation; a query that fails shows "Unknown".
/// </remarks>
public static class InstalledServices
{
    public const string ServiceKind = "Service";

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
                $"SELECT State, StartMode, DelayedAutoStart, ExitCode FROM Win32_Service WHERE Name = '{entry.Name}'");
            using var results = searcher.Get();
            var service = results.Cast<ManagementObject>().FirstOrDefault();
            if (service is null)
                return Row(entry, NotInstalled, "—", "—");

            var state = service["State"] as string;
            var startMode = service["StartMode"] as string;
            var delayed = service["DelayedAutoStart"] is true;
            var exitCode = service["ExitCode"] is uint code ? (int?)code : null;
            return Row(entry, ServiceStateText(state, startMode), ServiceLastResultText(state, exitCode), StartModeText(startMode, delayed));
        }
        catch (Exception)
        {
            return Row(entry, Unknown, Unknown, Unknown);
        }
    }

    private static FootprintStatus Row(FootprintEntry entry, string state, string lastResult, string trigger) =>
        new(entry.FriendlyName, entry.Name, entry.Kind, entry.Description, state, lastResult, trigger);

    // ── Plain words ─────────────────────────────────────────────

    public const string NotInstalled = "Not installed";
    public const string Unknown = "Unknown";

    /// <summary>Win32_Service.StartMode ("Auto", "Manual", "Disabled", "Boot", "System") in words.</summary>
    public static string StartModeText(string? startMode, bool delayed) => startMode switch
    {
        "Auto" when delayed => "Starts automatically (delayed) at boot",
        "Auto" or "Boot" or "System" => "Starts automatically at boot",
        "Manual" => "Starts manually",
        "Disabled" => "Disabled",
        _ => Unknown
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
    /// runs there is no last result to report.
    /// </summary>
    public static string ServiceLastResultText(string? state, int? exitCode) =>
        state == "Stopped" && exitCode is { } code ? $"Exit code {code}" : "—";
}
