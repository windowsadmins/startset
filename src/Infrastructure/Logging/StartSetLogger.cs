using Serilog;
using Serilog.Events;
using StartSet.Core.Constants;
using StartSet.Core.Models;

namespace StartSet.Infrastructure.Logging;

/// <summary>
/// Centralized logging configuration for StartSet.
/// Matches outset's logging patterns with Serilog implementation.
/// </summary>
public static class StartSetLogger
{
    private static ILogger? _logger;
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
    /// Creates the default logger configuration.
    /// </summary>
    private static ILogger CreateDefaultLogger()
    {
        return CreateLogger(StartSetPreferences.Default, false);
    }

    /// <summary>
    /// Creates a logger with the specified preferences.
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

        // File sink with daily rotation (30 files max, matching outset pattern)
        config.WriteTo.File(
            path: Paths.LogFilePath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: Paths.MaxLogFiles,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
            shared: true,
            flushToDiskInterval: TimeSpan.FromSeconds(1));

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
        }
    }

    // Convenience methods for static access
    public static void Debug(string message) => Logger.Debug(message);
    public static void Debug(string template, params object[] args) => Logger.Debug(template, args);
    public static void Information(string message) => Logger.Information(message);
    public static void Information(string template, params object[] args) => Logger.Information(template, args);
    public static void Warning(string message) => Logger.Warning(message);
    public static void Warning(string template, params object[] args) => Logger.Warning(template, args);
    public static void Warning(Exception ex, string message) => Logger.Warning(ex, message);
    public static void Error(string message) => Logger.Error(message);
    public static void Error(string template, params object[] args) => Logger.Error(template, args);
    public static void Error(Exception ex, string message) => Logger.Error(ex, message);
    public static void Error(Exception ex, string template, params object[] args) => Logger.Error(ex, template, args);
    public static void Fatal(string message) => Logger.Fatal(message);
    public static void Fatal(Exception ex, string message) => Logger.Fatal(ex, message);
}
