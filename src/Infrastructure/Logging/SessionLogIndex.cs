using System.Globalization;
using System.Text.Json;
using StartSet.Core.Constants;

namespace StartSet.Infrastructure.Logging;

/// <summary>One run's session folder: logs\YYYY-MM-DD\HHMM-runtype[_n]\.</summary>
public sealed record SessionLogEntry(string Day, string Folder, string Directory, DateTime? Started, string RunType)
{
    /// <summary>The human-readable log, startset.log.</summary>
    public string LogPath => Path.Combine(Directory, "startset.log");

    /// <summary>session.json, which carries the run's status.</summary>
    public string SessionFile => Path.Combine(Directory, "session.json");
}

/// <summary>
/// Reads the session tree <see cref="SessionLogger"/> writes, for the GUI's Logs tab and for
/// the Run tab to find the session a run it started is writing.
/// </summary>
public static class SessionLogIndex
{
    /// <summary>Every session under <paramref name="root"/>, newest first.</summary>
    public static List<SessionLogEntry> List(string? root = null)
    {
        var logRoot = root ?? Paths.LogDirectory;
        var entries = new List<SessionLogEntry>();
        if (!System.IO.Directory.Exists(logRoot))
            return entries;

        try
        {
            foreach (var dayDir in System.IO.Directory.GetDirectories(logRoot))
            {
                var day = Path.GetFileName(dayDir);
                if (!DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    continue;

                foreach (var sessionDir in System.IO.Directory.GetDirectories(dayDir))
                {
                    var folder = Path.GetFileName(sessionDir);
                    if (Parse(date, folder) is { } parsed)
                        entries.Add(new SessionLogEntry(day, folder, sessionDir, parsed.Started, parsed.RunType));
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return entries
            .OrderByDescending(e => e.Started ?? DateTime.MinValue)
            .ThenByDescending(e => e.Folder, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Splits "HHMM-runtype" or "HHMM-runtype_2" into its start time and run type. Null for a
    /// folder that is not a session.
    /// </summary>
    public static (DateTime Started, string RunType)? Parse(DateTime day, string folder)
    {
        if (folder.Length < 4 || !int.TryParse(folder.AsSpan(0, 2), out var hour) || !int.TryParse(folder.AsSpan(2, 2), out var minute))
            return null;
        if (hour > 23 || minute > 59)
            return null;

        var runType = folder.Length > 5 && folder[4] == '-' ? folder[5..] : "session";
        var suffix = runType.LastIndexOf('_');
        if (suffix > 0 && int.TryParse(runType.AsSpan(suffix + 1), out _))
            runType = runType[..suffix];

        return (day.Date.AddHours(hour).AddMinutes(minute), runType);
    }

    /// <summary>The session folders that exist now, to tell a new run's folder from the old ones.</summary>
    public static HashSet<string> Snapshot(string? root = null) =>
        List(root).Select(e => e.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The newest session of <paramref name="runType"/> that was not in <paramref name="before"/>.</summary>
    public static SessionLogEntry? FindNew(HashSet<string> before, string runType, string? root = null) =>
        List(root).FirstOrDefault(e =>
            !before.Contains(e.Directory) &&
            string.Equals(e.RunType, runType, StringComparison.OrdinalIgnoreCase));

    /// <summary>The status recorded in session.json ("running", "completed", ...), or null.</summary>
    public static string? ReadStatus(SessionLogEntry entry)
    {
        try
        {
            using var stream = new FileStream(entry.SessionFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("status", out var status) ? status.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
