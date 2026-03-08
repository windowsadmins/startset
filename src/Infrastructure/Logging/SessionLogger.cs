using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using StartSet.Core.Constants;

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

        // Create day-nested directory: logs/YYYY-MM-DD/HHMM/
        var dayDir = Path.Combine(Paths.LogDirectory, _sessionStart.ToString("yyyy-MM-dd"));
        var timeDir = _sessionStart.ToString("HHmm");
        _sessionDir = Path.Combine(dayDir, timeDir);

        // Handle same-minute collision by appending suffix
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

            // Clean up legacy flat log files (startset*.log from old Serilog rotation)
            foreach (var file in Directory.GetFiles(Paths.LogDirectory, "startset*.log"))
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTime < cutoff)
                        fileInfo.Delete();
                }
                catch { /* ignore */ }
            }
        }
        catch
        {
            // Silent failure - retention cleanup is non-critical
        }
    }

    private static bool IsDayDirectory(string name)
    {
        return name.Length == 10 && name[4] == '-' && name[7] == '-'
            && DateTime.TryParseExact(name, "yyyy-MM-dd", null,
                System.Globalization.DateTimeStyles.None, out _);
    }

    private static bool IsTimeSessionDirectory(string name)
    {
        // Primary: 4-digit HHMM (e.g. "1430")
        if (name.Length == 4 && int.TryParse(name, out var hhmm))
            return hhmm is >= 0 and <= 2359;

        // Collision suffix: HHMM_N (e.g. "1430_2")
        if (name.Length == 6 && name[4] == '_' && char.IsDigit(name[5]))
            return int.TryParse(name[..4], out var hhmm2) && hhmm2 is >= 0 and <= 2359;

        return false;
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
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to generate reports: {ex.Message}");
        }
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
