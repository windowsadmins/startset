using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using StartSet.Core.Constants;
using StartSet.Core.Enums;
using StartSet.Core.Models;

namespace StartSet.Infrastructure.Logging;

/// <summary>
/// Provides structured logging with day-nested timestamped directories,
/// matching Cimian's session logging pattern for external monitoring tool integration.
///
/// Directory structure: logs/YYYY-MM-DD/HHMM/
///   - startset.log    (human-readable log)
///   - session.json    (session metadata)
///   - events.jsonl    (structured event stream)
///
/// Reports: reports/
///   - sessions.json   (aggregated session summaries)
///   - events.json     (aggregated events from recent sessions)
///   - items.json      (one record per payload with its outcome this run)
///   - run.log         (latest session log copy)
/// </summary>
public class SessionLogger : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions JsonLinesOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private string _sessionId = "";
    private string _sessionDir = "";
    private DateTime _sessionStart;
    private string _runType = "manual";

    private StreamWriter? _logFile;         // startset.log
    private StreamWriter? _reportRunLog;    // reports/run.log
    private StreamWriter? _eventsFile;      // events.jsonl

    private readonly ConcurrentQueue<SessionEvent> _events = new();
    private readonly List<ItemRecord> _items = new();
    private SessionData _sessionData = new();
    private bool _disposed;

    private readonly object _logLock = new();

    /// <summary>
    /// Gets the current session ID (YYYY-MM-DD-HHMM format).
    /// </summary>
    public string SessionId => _sessionId;

    /// <summary>
    /// Gets the current session directory path.
    /// </summary>
    public string SessionDir => _sessionDir;

    /// <summary>
    /// Initializes a new session with a day-nested timestamped directory.
    /// </summary>
    /// <param name="runType">Type of run: boot, login, on-demand, service, cli</param>
    /// <returns>The session ID</returns>
    public string StartSession(string runType)
    {
        _sessionStart = DateTime.Now;
        _runType = runType;

        // Generate session ID as YYYY-MM-DD-HHMM
        _sessionId = _sessionStart.ToString("yyyy-MM-dd-HHmm");

        // Create day-nested directory: logs/YYYY-MM-DD/HHMM-runtype/
        //
        // The run type is in the directory name because several sessions run within
        // the same minute -- boot, login-window and login all start together at a
        // logon -- and without it they were distinguishable only by an arbitrary _2
        // or _3 suffix. Anyone looking for what the login payloads did had to open
        // each one to find out which was which, and picking "the newest" gave the
        // wrong session more often than not: it cost hours of a live investigation,
        // reading a boot session's log while diagnosing a stalled login batch and
        // concluding the wrong script was at fault.
        var dayDir = Path.Combine(Paths.LogDirectory, _sessionStart.ToString("yyyy-MM-dd"));
        var timeDir = $"{_sessionStart:HHmm}-{SanitizeRunType(runType)}";
        _sessionDir = Path.Combine(dayDir, timeDir);

        // Handle same-minute collision by appending suffix. With the run type in the
        // name this is now genuinely the same kind of session twice in one minute,
        // rather than three different kinds sharing a slot.
        if (Directory.Exists(_sessionDir))
        {
            for (var i = 2; i <= 9; i++)
            {
                var candidate = Path.Combine(dayDir, $"{timeDir}_{i}");
                if (!Directory.Exists(candidate))
                {
                    _sessionDir = candidate;
                    _sessionId = $"{_sessionStart:yyyy-MM-dd}-{timeDir}_{i}";
                    break;
                }
            }
        }
        else
        {
            _sessionId = $"{_sessionStart:yyyy-MM-dd}-{timeDir}";
        }

        Directory.CreateDirectory(_sessionDir);
        Directory.CreateDirectory(Paths.ReportsDirectory);

        // Perform retention cleanup (async, non-blocking)
        Task.Run(PerformRetentionCleanup);

        // Initialize log files
        InitializeLogFiles();

        // Initialize session data
        _sessionData = new SessionData
        {
            SessionId = _sessionId,
            StartTime = _sessionStart.ToString("o"),
            RunType = runType,
            Status = "running",
            Environment = GatherEnvironmentInfo()
        };

        // Write initial session.json
        WriteSessionFile();

        return _sessionId;
    }

    private void InitializeLogFiles()
    {
        try
        {
            // Main human-readable log
            var logPath = Path.Combine(_sessionDir, "startset.log");
            _logFile = new StreamWriter(logPath, append: true) { AutoFlush = true };

            // Report run log (reports/run.log - truncated each session)
            try
            {
                var reportRunLogPath = Path.Combine(Paths.ReportsDirectory, "run.log");
                if (File.Exists(reportRunLogPath))
                {
                    try { File.Delete(reportRunLogPath); } catch { /* ignore */ }
                }
                _reportRunLog = new StreamWriter(reportRunLogPath, append: false) { AutoFlush = true };
            }
            catch
            {
                _reportRunLog = null;
            }

            // Events file (JSON Lines format)
            var eventsPath = Path.Combine(_sessionDir, "events.jsonl");
            _eventsFile = new StreamWriter(eventsPath, append: true) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to initialize log files: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs a message to all log files.
    /// </summary>
    public void Log(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var formattedLine = $"[{timestamp}] {level,-5} {message}";

        lock (_logLock)
        {
            try
            {
                _logFile?.WriteLine(formattedLine);
                _reportRunLog?.WriteLine(formattedLine);
            }
            catch
            {
                // Silent failure
            }
        }
    }

    /// <summary>
    /// Logs a structured event for external monitoring tools.
    /// </summary>
    public void LogEvent(SessionEvent evt)
    {
        if (string.IsNullOrEmpty(evt.SessionId))
            evt.SessionId = _sessionId;

        if (evt.Timestamp == default)
            evt.Timestamp = DateTime.Now;

        if (string.IsNullOrEmpty(evt.EventId))
            evt.EventId = $"{_sessionId}-{DateTime.Now.Ticks}";

        _events.Enqueue(evt);

        try
        {
            var json = JsonSerializer.Serialize(evt, JsonLinesOptions);
            lock (_logLock)
            {
                _eventsFile?.WriteLine(json);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to write event: {ex.Message}");
        }
    }

    /// <summary>
    /// Convenience method to log a script execution event.
    /// </summary>
    public void LogScriptExecution(string scriptName, string action, string status,
        string message, long? durationMs = null, string? error = null)
    {
        LogEvent(new SessionEvent
        {
            EventType = "script_execution",
            ScriptName = scriptName,
            Action = action,
            Status = status,
            Message = message,
            DurationMs = durationMs,
            Error = error,
            Level = status == "failed" ? "ERROR" : (status == "completed" ? "INFO" : "DEBUG")
        });
    }

    /// <summary>
    /// Records the outcome of one payload for reports/items.json. Called once per
    /// payload the engine considered this session, skipped ones included. A payload
    /// seen twice in one session keeps its latest outcome.
    /// </summary>
    public void RecordPayloadOutcome(ScriptPayload script, ExecutionResult result)
    {
        var record = BuildItemRecord(script, result, _sessionId, DateTime.UtcNow);

        lock (_logLock)
        {
            _items.RemoveAll(i => i.Id == record.Id);
            _items.Add(record);
        }
    }

    /// <summary>
    /// Ends the current session and writes final summary.
    /// </summary>
    public void EndSession(string status, SessionSummary summary)
    {
        var endTime = DateTime.Now;
        var duration = endTime - _sessionStart;

        _sessionData.EndTime = endTime.ToString("o");
        _sessionData.Status = status;
        _sessionData.DurationSeconds = (long)duration.TotalSeconds;
        _sessionData.Summary = summary;

        // Write final session.json
        WriteSessionFile();

        // Generate reports
        GenerateReports();

        // Cleanup
        CloseLogFiles();
    }

    private void WriteSessionFile()
    {
        try
        {
            var sessionPath = Path.Combine(_sessionDir, "session.json");
            var json = JsonSerializer.Serialize(_sessionData, JsonOptions);
            File.WriteAllText(sessionPath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to write session.json: {ex.Message}");
        }
    }

    /// <summary>
    /// 30-day rolling retention cleanup.
    /// Removes day directories older than retention window and cleans up legacy flat logs.
    /// </summary>
    private static void PerformRetentionCleanup()
    {
        try
        {
            if (!Directory.Exists(Paths.LogDirectory))
                return;

            var cutoff = DateTime.Now.AddDays(-Paths.MaxRetentionDays);

            foreach (var entry in Directory.GetDirectories(Paths.LogDirectory))
            {
                var dirName = Path.GetFileName(entry);

                // Day directories (YYYY-MM-DD)
                if (IsDayDirectory(dirName))
                {
                    if (DateTime.TryParseExact(dirName, "yyyy-MM-dd", null,
                            System.Globalization.DateTimeStyles.None, out var dayDate)
                        && dayDate < cutoff.Date)
                    {
                        TryDeleteDirectory(entry);
                    }
                }
            }

            SweepExpiredFiles(Paths.LogDirectory, cutoff);

            // Verbose installer logs live in their own directory and are aged the same way.
            SweepExpiredFiles(Paths.InstallLogDirectory, cutoff);
        }
        catch
        {
            // Silent failure - retention cleanup is non-critical
        }
    }

    /// <summary>
    /// Deletes files sitting directly in <paramref name="directory"/> that were last
    /// written before <paramref name="cutoff"/>.
    /// </summary>
    /// <remarks>
    /// This used to glob "startset*.log", which covered the legacy Serilog rotation and
    /// nothing else. The log directory is shared: payload scripts write their own files
    /// here under their own names, and none of them were ever expired - the oldest on a
    /// long-running machine predate the retention window by months. Anything loose in
    /// the directory is a log, so age is the only test that needs applying.
    ///
    /// Deliberately not recursive. Session directories are aged as a unit by their own
    /// date-named rule above; picking old files out of one individually would leave half
    /// a session's log set behind.
    /// </remarks>
    public static void SweepExpiredFiles(string directory, DateTime cutoff)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.GetFiles(directory))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < cutoff)
                    info.Delete();
            }
            catch
            {
                // A file still held open by its writer throws here. Skip it and try
                // again next session rather than aborting the sweep.
            }
        }
    }

    private static bool IsDayDirectory(string name)
    {
        return name.Length == 10 && name[4] == '-' && name[7] == '-'
            && DateTime.TryParseExact(name, "yyyy-MM-dd", null,
                System.Globalization.DateTimeStyles.None, out _);
    }

    internal static bool IsTimeSessionDirectory(string name)
    {
        // Current: HHMM-runtype, optionally with a collision suffix
        // (e.g. "1430-login", "1430-login_2").
        //
        // Legacy forms are still accepted -- plain HHMM and HHMM_N -- because
        // retention and session lookup walk directories that already exist on every
        // machine, and refusing to recognise them would strand old sessions: never
        // cleaned up, and invisible to anyone looking for them.
        if (name.Length < 4)
            return false;

        if (!int.TryParse(name.AsSpan(0, 4), out var hhmm) || hhmm is < 0 or > 2359)
            return false;

        // Plain HHMM.
        if (name.Length == 4)
            return true;

        var rest = name.AsSpan(4);

        // Legacy collision suffix: _N
        if (rest.Length == 2 && rest[0] == '_' && char.IsDigit(rest[1]))
            return true;

        // Current: -runtype, with an optional _N on the end.
        if (rest[0] != '-')
            return false;

        var label = rest[1..];
        if (label.Length >= 2 && label[^2] == '_' && char.IsDigit(label[^1]))
            label = label[..^2];

        if (label.Length == 0)
            return false;

        foreach (var c in label)
        {
            if (!char.IsLetterOrDigit(c) && c != '-')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Reduces a run type to something safe for a directory name. Run types are
    /// internal constants ("boot", "login", "on-demand"), so this is a guard against
    /// a future caller passing something with a separator in it rather than an
    /// expected case.
    /// </summary>
    internal static string SanitizeRunType(string runType)
    {
        if (string.IsNullOrWhiteSpace(runType))
            return "session";

        var cleaned = new string(runType
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');

        return cleaned.Length == 0 ? "session" : cleaned;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Ignore - directory may be in use or protected
        }
    }

    /// <summary>
    /// Enumerates all session directories (day-nested), ordered newest-first.
    /// </summary>
    private static IEnumerable<string> EnumerateAllSessionDirs()
    {
        if (!Directory.Exists(Paths.LogDirectory))
            yield break;

        var dayDirs = Directory.GetDirectories(Paths.LogDirectory)
            .Where(d => IsDayDirectory(Path.GetFileName(d)))
            .OrderByDescending(d => Path.GetFileName(d));

        foreach (var dayDir in dayDirs)
        {
            var timeDirs = Directory.GetDirectories(dayDir)
                .Where(d => IsTimeSessionDirectory(Path.GetFileName(d)))
                .OrderByDescending(d => Path.GetFileName(d));

            foreach (var timeDir in timeDirs)
                yield return timeDir;
        }
    }

    /// <summary>
    /// Returns the latest session directory path.
    /// </summary>
    public static string? GetLatestSessionDir()
    {
        return EnumerateAllSessionDirs().FirstOrDefault();
    }

    private void GenerateReports()
    {
        try
        {
            GenerateSessionsReport();
            GenerateEventsReport();
            GenerateItemsReport();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to generate reports: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes reports/items.json: one record per payload this session considered, in
    /// the same shape Cimian writes for its managed items so one reader serves both.
    /// A session that considered no payloads leaves the previous file in place.
    /// </summary>
    private void GenerateItemsReport()
    {
        List<ItemRecord> items;
        lock (_logLock)
        {
            if (_items.Count == 0)
                return;
            items = _items.ToList();
        }

        var historyCutoff = DateTime.Now.AddDays(-ItemHistoryDays);
        var failures = CountRecentFailures(EnumerateAllSessionDirs().Take(ItemHistoryMaxSessions), historyCutoff);

        foreach (var item in items)
        {
            // The event stream is the durable count; the current run is folded in so a
            // failure still registers when script output logging is turned off.
            var failedThisRun = item.CurrentStatus == "Error" ? 1 : 0;
            item.FailureCount = Math.Max(failures.GetValueOrDefault(item.ItemName), failedThisRun);
        }

        WriteItemsReport(Paths.ReportsDirectory, items);
    }

    private const int ItemHistoryDays = 7;
    private const int ItemHistoryMaxSessions = 50;
    private const int MaxRecordedMessageLength = 500;

    /// <summary>
    /// Serialises <paramref name="items"/> to items.json inside <paramref name="reportsDir"/>.
    /// </summary>
    public static void WriteItemsReport(string reportsDir, IReadOnlyList<ItemRecord> items)
    {
        Directory.CreateDirectory(reportsDir);
        var itemsPath = Path.Combine(reportsDir, "items.json");
        File.WriteAllText(itemsPath, JsonSerializer.Serialize(items, JsonOptions));
    }

    /// <summary>
    /// Builds the items.json record for one payload outcome.
    /// </summary>
    /// <remarks>
    /// Pure so the mapping can be tested without a session on disk. Field semantics
    /// follow Cimian's items.json: <c>last_seen_in_session</c> is stamped only when this
    /// run acted on the payload, so a consumer can filter to what the run actually did;
    /// a skipped payload carries an empty session id and no <c>action_performed</c>.
    /// </remarks>
    public static ItemRecord BuildItemRecord(ScriptPayload script, ExecutionResult result, string sessionId, DateTime nowUtc)
    {
        // Neither a skipped nor a deferred payload performed an action.
        var acted = result.Status is not (ExecutionStatus.Skipped or ExecutionStatus.Deferred);
        var sessionStatus = result.Status switch
        {
            ExecutionStatus.Success => "completed",
            // A run-once payload that already ran is done, not waiting.
            ExecutionStatus.Skipped when script.AlreadyExecuted => "installed",
            ExecutionStatus.Skipped => "skipped",
            // Deferred is Pending, not Error. The payload did not run and its
            // settings are not applied -- so it must not report Installed -- but
            // it is a transient condition that the next sign-in retries, and
            // filing it as Error would raise a fleet-wide alarm for something
            // that heals itself. Pending says exactly what is true: outstanding.
            ExecutionStatus.Deferred => "pending",
            _ => "failed"
        };
        var status = NormalizeItemStatus(sessionStatus);
        var payloadPath = script.PayloadType.GetDirectoryPath();
        var payloadDir = payloadPath[(payloadPath.LastIndexOfAny(['\\', '/']) + 1)..];

        var record = new ItemRecord
        {
            Id = $"{payloadDir}/{script.FileName}".ToLowerInvariant().Replace(" ", ""),
            ItemName = script.FileName,
            DisplayName = Path.GetFileNameWithoutExtension(script.FileName),
            ItemType = script.Extension.TrimStart('.'),
            CurrentStatus = status,
            LatestVersion = "",
            InstalledVersion = null,
            LastSeenInSession = acted ? sessionId : "",
            LastAttemptTime = result.StartTime.UtcDateTime.ToString("o"),
            LastAttemptStatus = status,
            LastUpdate = nowUtc.ToString("o"),
            FailureCount = status == "Error" ? 1 : 0,
            Type = "startset",
            ActionPerformed = acted ? (script.IsPackage ? "install" : "execute") : null
        };

        if (status == "Error")
        {
            record.LastError = Truncate(
                FirstNonBlankLine(result.ErrorMessage)
                ?? FirstNonBlankLine(result.StandardError)
                ?? $"Exit code {result.ExitCode?.ToString() ?? "unknown"}");
        }

        // A payload that succeeded but wrote to stderr is the nearest thing a script
        // has to a warning, and the only one the session records.
        if (result.Status == ExecutionStatus.Success && FirstNonBlankLine(result.StandardError) is { } warning)
        {
            record.LastWarning = Truncate(warning);
            record.WarningCount = 1;
        }

        return record;
    }

    /// <summary>
    /// Counts failed script_execution events per payload name across the given session
    /// directories, ignoring events older than <paramref name="cutoff"/>. Mirrors the
    /// failure history Cimian folds into its items.json.
    /// </summary>
    public static Dictionary<string, int> CountRecentFailures(IEnumerable<string> sessionDirs, DateTime cutoff)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in sessionDirs)
        {
            var eventsPath = Path.Combine(dir, "events.jsonl");
            if (!File.Exists(eventsPath))
                continue;

            try
            {
                foreach (var line in File.ReadLines(eventsPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    SessionEvent? evt;
                    try { evt = JsonSerializer.Deserialize<SessionEvent>(line, JsonLinesOptions); }
                    catch { continue; }

                    if (evt == null || evt.Timestamp < cutoff || evt.EventType != "script_execution")
                        continue;
                    if (string.IsNullOrEmpty(evt.ScriptName) || evt.Status != "failed")
                        continue;

                    counts[evt.ScriptName] = counts.GetValueOrDefault(evt.ScriptName) + 1;
                }
            }
            catch
            {
                // A session still being written, or one we cannot read, contributes nothing.
            }
        }

        return counts;
    }

    /// <summary>
    /// Maps session outcomes onto the item status vocabulary Cimian's items.json uses.
    /// </summary>
    private static string NormalizeItemStatus(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "completed" or "success" or "installed" or "ok" => "Installed",
            "failed" or "error" or "fail" => "Error",
            "warning" or "warn" => "Warning",
            "pending" or "pending install" or "pending update" or "skipped" or "not installed" => "Pending",
            "removed" or "uninstalled" => "Removed",
            "not available" => "Not Available",
            _ => status switch
            {
                "Installed" or "Error" or "Warning" or "Pending" or "Removed" or "Not Available" => status,
                _ => "Pending"
            }
        };
    }

    private static string? FirstNonBlankLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
                return line.Trim();
        }

        return null;
    }

    private static string Truncate(string text)
    {
        return text.Length <= MaxRecordedMessageLength ? text : text[..MaxRecordedMessageLength];
    }

    private void GenerateSessionsReport()
    {
        var sessions = new List<SessionData>();

        foreach (var dir in EnumerateAllSessionDirs().Take(100))
        {
            var sessionPath = Path.Combine(dir, "session.json");
            if (File.Exists(sessionPath))
            {
                try
                {
                    var json = File.ReadAllText(sessionPath);
                    var session = JsonSerializer.Deserialize<SessionData>(json, JsonOptions);
                    if (session != null)
                        sessions.Add(session);
                }
                catch { /* Skip invalid session files */ }
            }
        }

        var sessionsPath = Path.Combine(Paths.ReportsDirectory, "sessions.json");
        File.WriteAllText(sessionsPath, JsonSerializer.Serialize(sessions, JsonOptions));
    }

    private void GenerateEventsReport()
    {
        var allEvents = new List<SessionEvent>();
        var cutoff = DateTime.Now.AddHours(-48);

        foreach (var dir in EnumerateAllSessionDirs().Take(10))
        {
            var eventsPath = Path.Combine(dir, "events.jsonl");
            if (File.Exists(eventsPath))
            {
                try
                {
                    foreach (var line in File.ReadLines(eventsPath))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            var evt = JsonSerializer.Deserialize<SessionEvent>(line, JsonLinesOptions);
                            if (evt != null && evt.Timestamp >= cutoff)
                                allEvents.Add(evt);
                        }
                    }
                }
                catch { /* Skip invalid event files */ }
            }
        }

        var eventsReportPath = Path.Combine(Paths.ReportsDirectory, "events.json");
        File.WriteAllText(eventsReportPath, JsonSerializer.Serialize(allEvents, JsonOptions));
    }

    private static Dictionary<string, object> GatherEnvironmentInfo()
    {
        return new Dictionary<string, object>
        {
            ["hostname"] = Environment.MachineName,
            ["user"] = Environment.UserName,
            ["os_version"] = Environment.OSVersion.ToString(),
            ["architecture"] = Environment.Is64BitOperatingSystem ? "x64" : "x86",
            ["process_id"] = Environment.ProcessId,
            ["log_version"] = "1.0"
        };
    }

    private void CloseLogFiles()
    {
        lock (_logLock)
        {
            _logFile?.Dispose();
            _logFile = null;
            _reportRunLog?.Dispose();
            _reportRunLog = null;
            _eventsFile?.Dispose();
            _eventsFile = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseLogFiles();
        GC.SuppressFinalize(this);
    }
}

// ────────────────────── Session Data Models ──────────────────────

/// <summary>
/// Session metadata written to session.json.
/// </summary>
public class SessionData
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("start_time")]
    public string StartTime { get; set; } = "";

    [JsonPropertyName("end_time")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndTime { get; set; }

    [JsonPropertyName("run_type")]
    public string RunType { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("duration_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long? DurationSeconds { get; set; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SessionSummary? Summary { get; set; }

    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Environment { get; set; }
}

/// <summary>
/// Session execution summary statistics.
/// </summary>
public class SessionSummary
{
    [JsonPropertyName("total_scripts")]
    public int TotalScripts { get; set; }

    [JsonPropertyName("succeeded")]
    public int Succeeded { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; set; }

    [JsonPropertyName("scripts_handled")]
    public List<string> ScriptsHandled { get; set; } = new();
}

/// <summary>
/// Structured event for events.jsonl.
/// </summary>
public class SessionEvent
{
    [JsonPropertyName("event_id")]
    public string EventId { get; set; } = "";

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; } = "INFO";

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = "";

    [JsonPropertyName("script_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScriptName { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("duration_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long? DurationMs { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

/// <summary>
/// One payload's outcome, written to reports/items.json. Field names match the record
/// Cimian writes for its managed items so a single reader serves both tools; the
/// <c>type</c> field is what tells them apart.
/// </summary>
public class ItemRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("item_name")]
    public string ItemName { get; set; } = "";

    [JsonPropertyName("display_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("item_type")]
    public string ItemType { get; set; } = "";

    [JsonPropertyName("current_status")]
    public string CurrentStatus { get; set; } = "";

    [JsonPropertyName("latest_version")]
    public string LatestVersion { get; set; } = "";

    [JsonPropertyName("installed_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstalledVersion { get; set; }

    [JsonPropertyName("last_seen_in_session")]
    public string LastSeenInSession { get; set; } = "";

    [JsonPropertyName("last_attempt_time")]
    public string LastAttemptTime { get; set; } = "";

    [JsonPropertyName("last_attempt_status")]
    public string LastAttemptStatus { get; set; } = "";

    [JsonPropertyName("last_update")]
    public string LastUpdate { get; set; } = "";

    [JsonPropertyName("failure_count")]
    public int FailureCount { get; set; }

    [JsonPropertyName("warning_count")]
    public int WarningCount { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "startset";

    [JsonPropertyName("last_error")]
    public string LastError { get; set; } = "";

    [JsonPropertyName("last_warning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastWarning { get; set; }

    [JsonPropertyName("action_performed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActionPerformed { get; set; }
}
