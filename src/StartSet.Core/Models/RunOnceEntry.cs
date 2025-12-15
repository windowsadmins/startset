using System.Text.Json.Serialization;

namespace StartSet.Core.Models;

/// <summary>
/// Tracks scripts that have been executed for run-once payloads.
/// Stored as JSON for easy inspection and modification.
/// </summary>
public class RunOnceEntry
{
    /// <summary>
    /// Full path to the script that was executed.
    /// </summary>
    [JsonPropertyName("script_path")]
    public required string ScriptPath { get; set; }

    /// <summary>
    /// SHA256 checksum of the script at execution time.
    /// Used to detect if script has changed and should re-run.
    /// </summary>
    [JsonPropertyName("checksum")]
    public required string Checksum { get; set; }

    /// <summary>
    /// Timestamp when the script was executed.
    /// </summary>
    [JsonPropertyName("executed_at")]
    public required DateTimeOffset ExecutedAt { get; set; }

    /// <summary>
    /// The payload type this execution was for.
    /// </summary>
    [JsonPropertyName("payload_type")]
    public required string PayloadType { get; set; }

    /// <summary>
    /// Exit code from the script execution.
    /// </summary>
    [JsonPropertyName("exit_code")]
    public int ExitCode { get; set; }

    /// <summary>
    /// Whether the execution was successful.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Username who executed the script (null for system context).
    /// </summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>
    /// Optional notes or error message from execution.
    /// </summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// Container for all run-once tracking data.
/// </summary>
public class RunOnceData
{
    /// <summary>
    /// Version of the run-once data format.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Last modification timestamp.
    /// </summary>
    [JsonPropertyName("last_modified")]
    public DateTimeOffset LastModified { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Dictionary of script filename to execution entry.
    /// </summary>
    [JsonPropertyName("entries")]
    public Dictionary<string, RunOnceEntry> Entries { get; set; } = new();
}
