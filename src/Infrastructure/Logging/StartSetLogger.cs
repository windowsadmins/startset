using Serilog;
using Serilog.Events;
using StartSet.Core.Constants;
using StartSet.Core.Models;

namespace StartSet.Infrastructure.Logging;

/// <summary>
/// Centralized logging configuration for StartSet.
/// Routes output to Serilog (console/EventLog) and SessionLogger (file-based session logs).
/// When a SessionLogger is attached, all log messages are also written to the session's startset.log.
/// </summary>
public static class StartSetLogger
{
    private static ILogger? _logger;
    private static SessionLogger? _sessionLogger;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets the configured logger instance.
    /// </summary>
    public static ILogger Logger
    {
        get
        {
            if (_logger == null)
            {
                lock (_lock)
                {
                    _logger ??= CreateDefaultLogger();
                }
            }
            return _logger;
        }
    }

    /// <summary>
    /// Gets the currently attached SessionLogger, if any.
    /// </summary>
    public static SessionLogger? Session => _sessionLogger;

    /// <summary>
    /// Initializes the logger with preferences.
    /// </summary>
    /// <param name="preferences">Configuration preferences</param>
    /// <param name="isService">Whether running as Windows Service</param>
    public static void Initialize(StartSetPreferences? preferences = null, bool isService = false)
    {
        lock (_lock)
        {
            _logger = CreateLogger(preferences ?? StartSetPreferences.Default, isService);
        }
    }

    /// <summary>
    /// Attaches a SessionLogger so all log output is also written to session log files.
    /// </summary>
    public static void SetSessionLogger(SessionLogger? logger)
    {
        _sessionLogger = logger;
    }

    /// <summary>
    /// Creates the default logger configuration.
    /// </summary>
    private static ILogger CreateDefaultLogger()
    {
        return CreateLogger(StartSetPreferences.Default, false);
    }

    /// <summary>
    /// Creates a logger with the specified preferences.
    /// File logging is handled by SessionLogger — Serilog handles console and EventLog only.
    /// </summary>
    private static ILogger CreateLogger(StartSetPreferences preferences, bool isService)
    {
        // Ensure log directory exists
        EnsureLogDirectory();

        // Determine minimum log level
        var minimumLevel = DetermineLogLevel(preferences);

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "StartSet");

        // Console sink (for CLI interactive mode)
        if (!isService)
        {
            config.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                restrictedToMinimumLevel: minimumLevel);
        }

        // Windows Event Log sink (for service mode and errors)
        if (isService || minimumLevel <= LogEventLevel.Warning)
        {
            config.WriteTo.EventLog(
                source: "StartSet",
                logName: "Application",
                restrictedToMinimumLevel: LogEventLevel.Warning);
        }

        return config.CreateLogger();
    }

    /// <summary>
    /// Determines the minimum log level from preferences.
    /// </summary>
    private static LogEventLevel DetermineLogLevel(StartSetPreferences preferences)
    {
        // Explicit log level takes precedence
        if (!string.IsNullOrEmpty(preferences.LogLevel))
        {
            return preferences.LogLevel.ToLowerInvariant() switch
            {
                "debug" or "verbose" => LogEventLevel.Debug,
                "information" or "info" => LogEventLevel.Information,
                "warning" or "warn" => LogEventLevel.Warning,
                "error" => LogEventLevel.Error,
                "fatal" => LogEventLevel.Fatal,
                _ => LogEventLevel.Information
            };
        }

        // Debug flag enables debug logging
        if (preferences.Debug)
            return LogEventLevel.Debug;

        // Verbose flag enables verbose/debug logging
        if (preferences.Verbose)
            return LogEventLevel.Debug;

        return LogEventLevel.Information;
    }

    /// <summary>
    /// Ensures the log directory exists.
    /// </summary>
    private static void EnsureLogDirectory()
    {
        try
        {
            if (!Directory.Exists(Paths.LogDirectory))
            {
                Directory.CreateDirectory(Paths.LogDirectory);
            }
        }
        catch
        {
            // Can't create log directory - will fail gracefully
        }
    }

    /// <summary>
    /// Closes and flushes the logger.
    /// </summary>
    public static void CloseAndFlush()
    {
        lock (_lock)
        {
            if (_logger is IDisposable disposable)
            {
                disposable.Dispose();
            }
            _logger = null;
            _sessionLogger = null;
        }
    }

    /// <summary>
    /// Writes to the session logger if attached, mapping Serilog level names to plain strings.
    /// </summary>
    private static void LogToSession(string level, string message)
    {
        _sessionLogger?.Log(level, message);
    }

    // Convenience methods for static access — all route to both Serilog and SessionLogger
    public static void Debug(string message) { Logger.Debug(message); LogToSession("DEBUG", message); }
    public static void Debug(string template, params object[] args) { Logger.Debug(template, args); LogToSession("DEBUG", FormatMessage(template, args)); }
    public static void Information(string message) { Logger.Information(message); LogToSession("INFO", message); }
    public static void Information(string template, params object[] args) { Logger.Information(template, args); LogToSession("INFO", FormatMessage(template, args)); }
    public static void Warning(string message) { Logger.Warning(message); LogToSession("WARN", message); }
    public static void Warning(string template, params object[] args) { Logger.Warning(template, args); LogToSession("WARN", FormatMessage(template, args)); }
    public static void Warning(Exception ex, string message) { Logger.Warning(ex, message); LogToSession("WARN", $"{message}: {ex.Message}"); }
    public static void Error(string message) { Logger.Error(message); LogToSession("ERROR", message); }
    public static void Error(string template, params object[] args) { Logger.Error(template, args); LogToSession("ERROR", FormatMessage(template, args)); }
    public static void Error(Exception ex, string message) { Logger.Error(ex, message); LogToSession("ERROR", $"{message}: {ex.Message}"); }
    public static void Error(Exception ex, string template, params object[] args) { Logger.Error(ex, template, args); LogToSession("ERROR", $"{FormatMessage(template, args)}: {ex.Message}"); }
    public static void Fatal(string message) { Logger.Fatal(message); LogToSession("FATAL", message); }
    public static void Fatal(Exception ex, string message) { Logger.Fatal(ex, message); LogToSession("FATAL", $"{message}: {ex.Message}"); }

    /// <summary>
    /// Simple message formatting for session log (replaces Serilog-style {Property} placeholders).
    /// </summary>
    private static string FormatMessage(string template, object[] args)
    {
        try
        {
            // Replace Serilog {Name} placeholders with {N} positional format
            var index = 0;
            var formatted = System.Text.RegularExpressions.Regex.Replace(
                template, @"\{[^}]+\}", _ => $"{{{index++}}}");
            return string.Format(formatted, args);
        }
        catch
        {
            return template;
        }
    }
}
