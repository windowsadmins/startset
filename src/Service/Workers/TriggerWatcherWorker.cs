using Microsoft.Extensions.Hosting;
using StartSet.Core.Constants;
using StartSet.Core.Enums;
using StartSet.Engine;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;
using StartSet.Infrastructure.Security;

namespace StartSet.Service.Workers;

/// <summary>
/// Worker that watches for trigger files and executes corresponding scripts.
/// </summary>
/// <remarks>
/// Trigger files are honoured in ScriptRoot and in TriggerDirectory, the one folder a
/// standard user may create files in. See <see cref="TriggerFiles"/> for which triggers
/// a standard user may fire.
/// </remarks>
public class TriggerWatcherWorker : BackgroundService
{
    private readonly PreferencesService _preferencesService;
    private readonly List<FileSystemWatcher> _watchers = [];

    public TriggerWatcherWorker(PreferencesService preferencesService)
    {
        _preferencesService = preferencesService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartSetLogger.Information("TriggerWatcherWorker starting");

        // Ensure script root exists
        ExecutionEngine.EnsureDirectories();

        // Set up file system watcher
        SetupWatcher();

        // Check for existing trigger files on startup
        await ProcessExistingTriggerFilesAsync(stoppingToken);

        // Keep running until cancelled
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private void SetupWatcher()
    {
        foreach (var directory in TriggerFiles.Directories)
        {
            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    Filter = ".startset.*",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };

                watcher.Created += OnTriggerFileCreated;
                _watchers.Add(watcher);
                StartSetLogger.Debug("Trigger file watcher started for {Path}", directory);
            }
            catch (Exception ex)
            {
                StartSetLogger.Error(ex, "Failed to set up trigger file watcher for {Path}", directory);
            }
        }
    }

    /// <summary>
    /// Whether the trigger may be acted on. A privileged trigger a standard user created is
    /// deleted here, unused, and the reason logged.
    /// </summary>
    private static bool Admit(string path)
    {
        var decision = TriggerFiles.Evaluate(path, FileTrust.OwnerOf(path));
        if (decision.IsTrusted)
            return true;

        StartSetLogger.Warning("Ignoring trigger: {Reason}", decision.Reason ?? path);
        try { File.Delete(path); } catch { }
        return false;
    }

    private async void OnTriggerFileCreated(object sender, FileSystemEventArgs e)
    {
        StartSetLogger.Information("Trigger file detected: {File}", e.Name ?? e.FullPath);

        // Small delay to ensure file is fully written
        await Task.Delay(100);

        var payloadTypes = GetPayloadTypesForTrigger(e.FullPath);
        if (payloadTypes.Length == 0)
        {
            StartSetLogger.Warning("Unknown trigger file: {File}", e.Name ?? "Unknown");
            return;
        }

        if (!Admit(e.FullPath))
            return;

        // Pick up any policy or settings change made since the last run.
        _preferencesService.Reload();

        using var session = new SessionLogger();
        session.StartSession("trigger");
        StartSetLogger.SetSessionLogger(session);

        try
        {
            var engine = new ExecutionEngine(_preferencesService);
            var results = await engine.ExecuteAsync(payloadTypes);

            session.EndSession(
                results.Any(r => r.Status == ExecutionStatus.Failed) ? "completed_with_errors" : "completed",
                new SessionSummary
                {
                    TotalScripts = results.Count,
                    Succeeded = results.Count(r => r.Status == ExecutionStatus.Success),
                    Failed = results.Count(r => r.Status == ExecutionStatus.Failed),
                    Skipped = results.Count(r => r.Status == ExecutionStatus.Skipped),
                    ScriptsHandled = results.Select(r => r.Script.FileName).ToList()
                });

            // Delete trigger file after processing
            try
            {
                if (File.Exists(e.FullPath))
                    File.Delete(e.FullPath);
            }
            catch { }
        }
        catch (Exception ex)
        {
            StartSetLogger.Error(ex, "Error processing trigger file: {File}", e.Name ?? "Unknown");
            session.EndSession("failed", new SessionSummary());
        }
        finally
        {
            StartSetLogger.SetSessionLogger(null);
        }
    }

    private async Task ProcessExistingTriggerFilesAsync(CancellationToken stoppingToken)
    {
        var triggerFiles = new[]
        {
            (Paths.TriggerOnDemand, new[] { PayloadType.OnDemand }),
            (Paths.TriggerOnDemandPrivileged, new[] { PayloadType.OnDemandPrivileged }),
            (Paths.TriggerLogin, new[] { PayloadType.LoginOnce, PayloadType.LoginEvery }),
            (Paths.TriggerLoginPrivileged, new[] { PayloadType.LoginPrivilegedOnce, PayloadType.LoginPrivilegedEvery }),
            (Paths.TriggerCleanup, Array.Empty<PayloadType>())
        };

        var locations = triggerFiles.SelectMany(t =>
            TriggerFiles.LocationsOf(t.Item1).Select(location => (Path: location, Name: t.Item1, Types: t.Item2)));

        foreach (var (path, name, payloadTypes) in locations)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            if (File.Exists(path) && Admit(path))
            {
                StartSetLogger.Information("Processing existing trigger file: {File}", path);

                if (name == Paths.TriggerCleanup)
                {
                    ExecutionEngine.CleanupTriggerFiles();
                }
                else if (payloadTypes.Length > 0)
                {
                    var engine = new ExecutionEngine(_preferencesService);
                    await engine.ExecuteAsync(payloadTypes, cancellationToken: stoppingToken);
                }

                try { File.Delete(path); } catch { }
            }
        }
    }

    /// <summary>
    /// Maps a trigger file to the payloads it runs.
    ///
    /// .startset.login is the user-context counterpart of .startset.login-privileged:
    /// it re-runs the login payloads in the signed-in user's session without waiting
    /// for a logon. That matters because a package whose payload is login-every has
    /// no other way to take effect on a machine that is already signed in -- the
    /// files are installed, and nothing runs them until someone logs out and back
    /// in. On a shared lab machine that can be days.
    ///
    /// Safe by construction rather than by care: LoginOnce and LoginEvery are
    /// IsUserContext, so UserContextExecution.TryRunAsConsoleUser owns them. With
    /// nobody signed in they are reported Deferred, never run as SYSTEM. Triggering
    /// this at the login window is therefore a no-op rather than a way to land HKCU
    /// writes in SYSTEM's hive.
    ///
    /// LoginOnce still honours its run-once record, so this replays only what has
    /// not already run for that user.
    /// </summary>
    private static PayloadType[] GetPayloadTypesForTrigger(string triggerPath) => Path.GetFileName(triggerPath) switch
    {
        var n when n == Path.GetFileName(Paths.TriggerOnDemand) => [PayloadType.OnDemand],
        var n when n == Path.GetFileName(Paths.TriggerOnDemandPrivileged) => [PayloadType.OnDemandPrivileged],
        var n when n == Path.GetFileName(Paths.TriggerLogin) => [PayloadType.LoginOnce, PayloadType.LoginEvery],
        var n when n == Path.GetFileName(Paths.TriggerLoginPrivileged) => [PayloadType.LoginPrivilegedOnce, PayloadType.LoginPrivilegedEvery],
        _ => []
    };

    public override void Dispose()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        base.Dispose();
    }
}
