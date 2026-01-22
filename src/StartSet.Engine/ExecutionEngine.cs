using StartSet.Core.Constants;
using StartSet.Core.Enums;
using StartSet.Core.Models;
using StartSet.Engine.Interfaces;
using StartSet.Engine.Processors;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;
using StartSet.Infrastructure.Network;
using StartSet.Infrastructure.Tracking;
using StartSet.Infrastructure.Validation;

namespace StartSet.Engine;

/// <summary>
/// Main execution engine for StartSet.
/// Coordinates script discovery, validation, and execution.
/// </summary>
public class ExecutionEngine
{
    private readonly PreferencesService _preferencesService;
    private readonly ChecksumService _checksumService;
    private readonly PermissionValidator _permissionValidator;
    private readonly NetworkMonitor _networkMonitor;
    private readonly List<IScriptProcessor> _processors;

    public ExecutionEngine(PreferencesService preferencesService)
    {
        _preferencesService = preferencesService;
        _checksumService = new ChecksumService();
        _permissionValidator = new PermissionValidator();
        _networkMonitor = new NetworkMonitor(_preferencesService.Preferences.NetworkTimeout);

        // Register all processors
        _processors =
        [
            new PowerShellProcessor(),
            new BatchProcessor(),
            new ExecutableProcessor(),
            new PackageProcessor()
        ];
    }

    /// <summary>
    /// Executes all scripts for the specified payload types.
    /// </summary>
    /// <param name="payloadTypes">Payload types to execute</param>
    /// <param name="username">Username for run-once tracking (null for system)</param>
    /// <param name="waitForNetwork">Whether to wait for network connectivity</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of execution results</returns>
    public async Task<List<ExecutionResult>> ExecuteAsync(
        PayloadType[] payloadTypes,
        string? username = null,
        bool? waitForNetwork = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ExecutionResult>();
        var prefs = _preferencesService.Preferences;

        // Check if user is in ignored list (for user-context payload types)
        if (!string.IsNullOrEmpty(username) && IsUserIgnored(username, prefs))
        {
            StartSetLogger.Information("User {Username} is in ignored_users list, skipping execution", username);
            return results;
        }

        StartSetLogger.Information("Starting execution for payload types: {Types}",
            string.Join(", ", payloadTypes.Select(p => p.ToString())));

        // Ensure directories exist
        EnsureDirectories();

        // Wait for network if required
        if (waitForNetwork ?? prefs.WaitForNetwork)
        {
            var networkAvailable = await _networkMonitor.WaitForNetworkAsync(cancellationToken);
            if (!networkAvailable && !prefs.IgnoreNetworkFailure)
            {
                StartSetLogger.Warning("Network not available, aborting execution");
                return results;
            }
        }

        // Execute each payload type
        foreach (var payloadType in payloadTypes)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var payloadResults = await ExecutePayloadTypeAsync(payloadType, username, cancellationToken);
            results.AddRange(payloadResults);
        }

        // Summary logging
        var succeeded = results.Count(r => r.Status == ExecutionStatus.Success);
        var failed = results.Count(r => r.Status == ExecutionStatus.Failed);
        var skipped = results.Count(r => r.Status == ExecutionStatus.Skipped);

        StartSetLogger.Information("Execution complete: {Succeeded} succeeded, {Failed} failed, {Skipped} skipped",
            succeeded, failed, skipped);

        return results;
    }

    /// <summary>
    /// Executes all scripts for a specific payload type.
    /// </summary>
    private async Task<List<ExecutionResult>> ExecutePayloadTypeAsync(
        PayloadType payloadType,
        string? username,
        CancellationToken cancellationToken)
    {
        var results = new List<ExecutionResult>();
        var prefs = _preferencesService.Preferences;
        var directory = payloadType.GetDirectoryPath();

        StartSetLogger.Debug("Processing payload type: {Type} in {Directory}", payloadType, directory);

        if (!Directory.Exists(directory))
        {
            StartSetLogger.Debug("Directory does not exist: {Directory}", directory);
            return results;
        }

        // Discover scripts
        var scripts = DiscoverScripts(directory, payloadType);
        if (scripts.Count == 0)
        {
            StartSetLogger.Debug("No scripts found in {Directory}", directory);
            return results;
        }

        StartSetLogger.Information("Found {Count} scripts in {Directory}", scripts.Count, directory);

        // Get run-once tracker
        var runOnceTracker = payloadType.IsRunOnce()
            ? new RunOnceTracker(payloadType.IsUserContext() ? username : null)
            : null;

        // Filter already-executed scripts (respecting overrides)
        foreach (var script in scripts)
        {
            if (payloadType.IsRunOnce() && runOnceTracker != null)
            {
                // Check if script is overridden (force re-run)
                if (IsScriptOverridden(script.FilePath, prefs))
                {
                    StartSetLogger.Information("Script {Script} is in overrides list, will re-run", script.FileName);
                    continue; // Don't skip, even if previously executed
                }

                if (runOnceTracker.HasExecuted(script.FilePath, script.Checksum))
                {
                    script.ShouldSkip = true;
                    script.SkipReason = "Already executed (run-once)";
                }
            }
        }

        // Execute scripts
        var timeout = TimeSpan.FromSeconds(prefs.ScriptTimeout);

        foreach (var script in scripts.OrderBy(s => s.SortOrder))
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            ExecutionResult result;

            if (script.ShouldSkip)
            {
                result = ExecutionResult.Skipped(script, script.SkipReason ?? "Unknown reason");
                StartSetLogger.Debug("Skipping script: {Script} - {Reason}", script.FileName, script.SkipReason ?? "Unknown");
            }
            else
            {
                result = await ExecuteScriptAsync(script, timeout, cancellationToken);

                // Track run-once execution
                if (payloadType.IsRunOnce() && runOnceTracker != null && result.IsSuccess)
                {
                    runOnceTracker.RecordExecution(result);
                }

                // Delete boot-once scripts after execution
                if (payloadType.DeleteAfterExecution() && result.IsSuccess)
                {
                    try
                    {
                        File.Delete(script.FilePath);
                        StartSetLogger.Information("Deleted boot-once script: {Script}", script.FileName);
                    }
                    catch (Exception ex)
                    {
                        StartSetLogger.Warning("Failed to delete boot-once script {Script}: {Error}", script.FileName, ex.Message);
                    }
                }
            }

            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Discovers scripts in a directory.
    /// </summary>
    private List<ScriptPayload> DiscoverScripts(string directory, PayloadType payloadType)
    {
        var scripts = new List<ScriptPayload>();
        var prefs = _preferencesService.Preferences;
        var allowedExtensions = prefs.AllowedExtensions.Select(e => e.ToLowerInvariant()).ToHashSet();

        try
        {
            var files = Directory.GetFiles(directory)
                .Where(f => allowedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

            var sortOrder = 0;
            foreach (var filePath in files)
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    var script = new ScriptPayload
                    {
                        FilePath = filePath,
                        PayloadType = payloadType,
                        FileSize = fileInfo.Length,
                        LastModified = fileInfo.LastWriteTimeUtc,
                        SortOrder = sortOrder++
                    };

                    // Compute checksum
                    script.Checksum = ChecksumService.ComputeChecksum(filePath);

                    // Validate checksum if enabled
                    if (prefs.ChecksumValidation && !_checksumService.ValidateChecksum(filePath))
                    {
                        script.ShouldSkip = true;
                        script.SkipReason = "Checksum validation failed";
                    }

                    // Validate permissions for elevated scripts
                    if (payloadType.RequiresElevation())
                    {
                        var permResult = _permissionValidator.ValidateScript(filePath, payloadType);
                        if (!permResult.IsValid)
                        {
                            script.ShouldSkip = true;
                            script.SkipReason = $"Permission validation failed: {permResult.Error}";
                        }
                    }

                    scripts.Add(script);
                }
                catch (Exception ex)
                {
                    StartSetLogger.Warning("Error loading script {FilePath}: {Error}", filePath, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            StartSetLogger.Error(ex, "Error discovering scripts in {Directory}", directory);
        }

        return scripts;
    }

    /// <summary>
    /// Executes a single script.
    /// </summary>
    private async Task<ExecutionResult> ExecuteScriptAsync(
        ScriptPayload script,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // Find appropriate processor
        var processor = _processors.FirstOrDefault(p => p.CanProcess(script));

        if (processor == null)
        {
            StartSetLogger.Warning("No processor found for script type: {Extension}", script.Extension);
            return new ExecutionResult
            {
                Script = script,
                Status = ExecutionStatus.UnsupportedType,
                ErrorMessage = $"No processor found for extension: {script.Extension}",
                StartTime = DateTimeOffset.UtcNow
            };
        }

        return await processor.ExecuteAsync(script, timeout, cancellationToken);
    }

    /// <summary>
    /// Ensures all required directories exist.
    /// </summary>
    public static void EnsureDirectories()
    {
        foreach (var directory in Paths.AllPayloadDirectories)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    StartSetLogger.Debug("Created directory: {Directory}", directory);
                }
            }
            catch (Exception ex)
            {
                StartSetLogger.Warning("Failed to create directory {Directory}: {Error}", directory, ex.Message);
            }
        }
    }

    /// <summary>
    /// Cleans up on-demand trigger files.
    /// </summary>
    public static void CleanupTriggerFiles()
    {
        var triggerFiles = new[]
        {
            Paths.TriggerOnDemand,
            Paths.TriggerOnDemandPrivileged,
            Paths.TriggerLoginPrivileged,
            Paths.TriggerCleanup
        };

        foreach (var triggerFile in triggerFiles)
        {
            try
            {
                if (File.Exists(triggerFile))
                {
                    File.Delete(triggerFile);
                    StartSetLogger.Debug("Deleted trigger file: {File}", triggerFile);
                }
            }
            catch (Exception ex)
            {
                StartSetLogger.Warning("Failed to delete trigger file {File}: {Error}", triggerFile, ex.Message);
            }
        }
    }

    /// <summary>
    /// Checks if a user is in the ignored users list.
    /// </summary>
    private static bool IsUserIgnored(string username, StartSetPreferences prefs)
    {
        if (prefs.IgnoredUsers.Count == 0)
            return false;

        return prefs.IgnoredUsers.Any(u =>
            string.Equals(u, username, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if a script is in the overrides list (should re-run even if executed before).
    /// </summary>
    private static bool IsScriptOverridden(string scriptPath, StartSetPreferences prefs)
    {
        if (prefs.Overrides.Count == 0)
            return false;

        var fileName = Path.GetFileName(scriptPath).ToLowerInvariant();
        return prefs.Overrides.Any(o =>
            string.Equals(Path.GetFileName(o).ToLowerInvariant(), fileName, StringComparison.OrdinalIgnoreCase));
    }
}
