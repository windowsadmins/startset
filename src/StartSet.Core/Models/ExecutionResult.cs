using StartSet.Core.Enums;

namespace StartSet.Core.Models;

/// <summary>
/// Result of a script execution.
/// </summary>
public class ExecutionResult
{
    /// <summary>
    /// The script that was executed.
    /// </summary>
    public required ScriptPayload Script { get; set; }

    /// <summary>
    /// Execution status.
    /// </summary>
    public required ExecutionStatus Status { get; set; }

    /// <summary>
    /// Process exit code (null if not executed).
    /// </summary>
    public int? ExitCode { get; set; }

    /// <summary>
    /// Standard output from the script.
    /// </summary>
    public string? StandardOutput { get; set; }

    /// <summary>
    /// Standard error from the script.
    /// </summary>
    public string? StandardError { get; set; }

    /// <summary>
    /// Time when execution started.
    /// </summary>
    public DateTimeOffset StartTime { get; set; }

    /// <summary>
    /// Time when execution completed.
    /// </summary>
    public DateTimeOffset? EndTime { get; set; }

    /// <summary>
    /// Duration of execution.
    /// </summary>
    public TimeSpan? Duration => EndTime.HasValue ? EndTime.Value - StartTime : null;

    /// <summary>
    /// Error message if execution failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Exception details if one was thrown.
    /// </summary>
    public Exception? Exception { get; set; }

    /// <summary>
    /// Whether the execution was successful.
    /// </summary>
    public bool IsSuccess => Status == ExecutionStatus.Success;

    /// <summary>
    /// Creates a success result.
    /// </summary>
    public static ExecutionResult Success(ScriptPayload script, int exitCode, string? stdout = null, string? stderr = null) => new()
    {
        Script = script,
        Status = ExecutionStatus.Success,
        ExitCode = exitCode,
        StandardOutput = stdout,
        StandardError = stderr,
        StartTime = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Creates a failure result.
    /// </summary>
    public static ExecutionResult Failed(ScriptPayload script, int exitCode, string? errorMessage = null) => new()
    {
        Script = script,
        Status = ExecutionStatus.Failed,
        ExitCode = exitCode,
        ErrorMessage = errorMessage,
        StartTime = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Creates a skipped result.
    /// </summary>
    public static ExecutionResult Skipped(ScriptPayload script, string reason) => new()
    {
        Script = script,
        Status = ExecutionStatus.Skipped,
        ErrorMessage = reason,
        StartTime = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Creates a timeout result.
    /// </summary>
    public static ExecutionResult Timeout(ScriptPayload script, TimeSpan elapsed) => new()
    {
        Script = script,
        Status = ExecutionStatus.Timeout,
        ErrorMessage = $"Script execution timed out after {elapsed.TotalSeconds:F1} seconds",
        StartTime = DateTimeOffset.UtcNow
    };
}
