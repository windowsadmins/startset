using Microsoft.Extensions.Hosting;
using StartSet.Core.Constants;
using StartSet.Core.Enums;
using StartSet.Engine;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;

namespace StartSet.Service.Workers;

/// <summary>
/// Worker that watches for trigger files and executes corresponding scripts.
/// </summary>
public class TriggerWatcherWorker : BackgroundService
{
    private readonly PreferencesService _preferencesService;
    private FileSystemWatcher? _watcher;

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
        try
        {
            _watcher = new FileSystemWatcher(Paths.ScriptRoot)
            {
                Filter = ".startset.*",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnTriggerFileCreated;
            StartSetLogger.Debug("Trigger file watcher started for {Path}", Paths.ScriptRoot);
        }
        catch (Exception ex)
        {
            StartSetLogger.Error(ex, "Failed to set up trigger file watcher");
        }
    }

    private async void OnTriggerFileCreated(object sender, FileSystemEventArgs e)
    {
        StartSetLogger.Information("Trigger file detected: {File}", e.Name);

        // Small delay to ensure file is fully written
        await Task.Delay(100);

        var payloadTypes = GetPayloadTypesForTrigger(e.FullPath);
        if (payloadTypes.Length == 0)
        {
            StartSetLogger.Warning("Unknown trigger file: {File}", e.Name);
            return;
        }

        try
        {
            var engine = new ExecutionEngine(_preferencesService);
            await engine.ExecuteAsync(payloadTypes);

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
            StartSetLogger.Error(ex, "Error processing trigger file: {File}", e.Name);
        }
    }

    private async Task ProcessExistingTriggerFilesAsync(CancellationToken stoppingToken)
    {
        var triggerFiles = new[]
        {
            (Paths.TriggerOnDemand, new[] { PayloadType.OnDemand }),
            (Paths.TriggerOnDemandPrivileged, new[] { PayloadType.OnDemandPrivileged }),
            (Paths.TriggerLoginPrivileged, new[] { PayloadType.LoginPrivilegedOnce, PayloadType.LoginPrivilegedEvery }),
            (Paths.TriggerCleanup, Array.Empty<PayloadType>())
        };

        foreach (var (path, payloadTypes) in triggerFiles)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            if (File.Exists(path))
            {
                StartSetLogger.Information("Processing existing trigger file: {File}", path);

                if (path == Paths.TriggerCleanup)
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

    private static PayloadType[] GetPayloadTypesForTrigger(string triggerPath) => triggerPath switch
    {
        var p when p == Paths.TriggerOnDemand => [PayloadType.OnDemand],
        var p when p == Paths.TriggerOnDemandPrivileged => [PayloadType.OnDemandPrivileged],
        var p when p == Paths.TriggerLoginPrivileged => [PayloadType.LoginPrivilegedOnce, PayloadType.LoginPrivilegedEvery],
        _ => []
    };

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }
}
