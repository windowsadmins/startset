using System.Text.Json;
using System.Text.Json.Serialization;
using StartSet.Core.Constants;
using StartSet.Core.Models;
using StartSet.Infrastructure.Logging;

namespace StartSet.Infrastructure.Tracking;

/// <summary>
/// Service for tracking run-once script executions.
/// Uses JSON for persistence (easy to inspect and modify).
/// </summary>
public class RunOnceTracker
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private RunOnceData _data;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a run-once tracker for the specified user.
    /// </summary>
    /// <param name="username">Username (null for system-wide tracking)</param>
    public RunOnceTracker(string? username = null)
    {
        _filePath = Paths.GetRunOnceFilePath(username);
        _data = LoadData();
    }

    /// <summary>
    /// Creates a run-once tracker with a custom file path.
    /// </summary>
    /// <param name="filePath">Full path to the tracking file</param>
    /// <param name="isCustomPath">Must be true (to differentiate from username constructor)</param>
    public RunOnceTracker(string filePath, bool isCustomPath)
    {
        if (!isCustomPath) throw new ArgumentException("Use constructor with just username for standard paths");
        _filePath = filePath;
        _data = LoadData();
    }

    /// <summary>
    /// Checks if a script has already been executed.
    /// </summary>
    /// <param name="scriptPath">Path to the script</param>
    /// <param name="currentChecksum">Optional current checksum to check for changes</param>
    /// <returns>True if already executed (and unchanged if checksum provided)</returns>
    public bool HasExecuted(string scriptPath, string? currentChecksum = null)
    {
        lock (_lock)
        {
            var key = GetKey(scriptPath);
            if (!_data.Entries.TryGetValue(key, out var entry))
                return false;

            // If checksum provided, verify script hasn't changed
            if (currentChecksum != null && !string.Equals(entry.Checksum, currentChecksum, StringComparison.OrdinalIgnoreCase))
            {
                StartSetLogger.Information("Script {Script} has changed since last execution, will re-run", scriptPath);
                return false;
            }

            return entry.Success;
        }
    }

    /// <summary>
    /// Records a script execution.
    /// </summary>
    public void RecordExecution(ExecutionResult result)
    {
        lock (_lock)
        {
            var key = GetKey(result.Script.FilePath);

            _data.Entries[key] = new RunOnceEntry
            {
                ScriptPath = result.Script.FilePath,
                Checksum = result.Script.Checksum ?? "",
                ExecutedAt = result.StartTime,
                PayloadType = result.Script.PayloadType.ToString(),
                ExitCode = result.ExitCode ?? -1,
                Success = result.IsSuccess,
                Username = Environment.UserName,
                Notes = result.ErrorMessage
            };

            _data.LastModified = DateTimeOffset.UtcNow;
            SaveData();
        }
    }

    /// <summary>
    /// Manually marks a script as executed.
    /// </summary>
    public void MarkAsExecuted(string scriptPath, string checksum, string payloadType)
    {
        lock (_lock)
        {
            var key = GetKey(scriptPath);

            _data.Entries[key] = new RunOnceEntry
            {
                ScriptPath = scriptPath,
                Checksum = checksum,
                ExecutedAt = DateTimeOffset.UtcNow,
                PayloadType = payloadType,
                ExitCode = 0,
                Success = true,
                Username = Environment.UserName,
                Notes = "Manually marked as executed"
            };

            _data.LastModified = DateTimeOffset.UtcNow;
            SaveData();
        }
    }

    /// <summary>
    /// Removes a script from the run-once tracking.
    /// </summary>
    public bool ClearExecution(string scriptPath)
    {
        lock (_lock)
        {
            var key = GetKey(scriptPath);
            if (_data.Entries.Remove(key))
            {
                _data.LastModified = DateTimeOffset.UtcNow;
                SaveData();
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Clears all run-once tracking data.
    /// </summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _data = new RunOnceData();
            SaveData();
        }
    }

    /// <summary>
    /// Gets all tracked executions.
    /// </summary>
    public IReadOnlyDictionary<string, RunOnceEntry> GetAllExecutions()
    {
        lock (_lock)
        {
            return _data.Entries.AsReadOnly();
        }
    }

    /// <summary>
    /// Gets the count of tracked executions.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _data.Entries.Count;
            }
        }
    }

    /// <summary>
    /// Reloads data from disk.
    /// </summary>
    public void Reload()
    {
        lock (_lock)
        {
            _data = LoadData();
        }
    }

    private RunOnceData LoadData()
    {
        if (!File.Exists(_filePath))
            return new RunOnceData();

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<RunOnceData>(json, _jsonOptions) ?? new RunOnceData();
        }
        catch (Exception ex)
        {
            StartSetLogger.Warning("Failed to load run-once data from {Path}, starting fresh: {Error}", _filePath, ex.Message);
            return new RunOnceData();
        }
    }

    private void SaveData()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_data, _jsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            StartSetLogger.Error(ex, "Failed to save run-once data to {Path}", _filePath);
        }
    }

    private static string GetKey(string scriptPath)
    {
        // Use filename as key (allows moving scripts between directories)
        return Path.GetFileName(scriptPath).ToLowerInvariant();
    }
}
