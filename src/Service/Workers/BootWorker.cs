using Microsoft.Extensions.Hosting;
using StartSet.Core.Enums;
using StartSet.Engine;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;

namespace StartSet.Service.Workers;

/// <summary>
/// Worker that runs boot scripts when the service starts.
/// </summary>
public class BootWorker : BackgroundService
{
    private readonly PreferencesService _preferencesService;

    public BootWorker(PreferencesService preferencesService)
    {
        _preferencesService = preferencesService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartSetLogger.Information("BootWorker starting - executing boot scripts");

        using var session = new SessionLogger();
        session.StartSession("boot");
        StartSetLogger.SetSessionLogger(session);

        try
        {
            // Small delay to allow service to fully start
            await Task.Delay(1000, stoppingToken);

            var engine = new ExecutionEngine(_preferencesService);
            
            // Execute boot scripts (with network wait if configured)
            var results = await engine.ExecuteAsync(
                [PayloadType.BootOnce, PayloadType.BootEvery],
                username: null,
                waitForNetwork: null, // Use preference
                cancellationToken: stoppingToken);

            var succeeded = results.Count(r => r.Status == ExecutionStatus.Success);
            var failed = results.Count(r => r.Status == ExecutionStatus.Failed);
            var skipped = results.Count(r => r.Status == ExecutionStatus.Skipped);

            StartSetLogger.Information(
                "Boot scripts complete: {Succeeded} succeeded, {Failed} failed, {Skipped} skipped",
                succeeded, failed, skipped);

            // Execute login-window scripts if any
            var loginWindowResults = await engine.ExecuteAsync(
                [PayloadType.LoginWindow],
                username: null,
                waitForNetwork: false,
                cancellationToken: stoppingToken);

            if (loginWindowResults.Count > 0)
            {
                StartSetLogger.Information("Login-window scripts complete: {Count} processed", loginWindowResults.Count);
            }

            var allResults = results.Concat(loginWindowResults).ToList();
            session.EndSession(
                allResults.Any(r => r.Status == ExecutionStatus.Failed) ? "completed_with_errors" : "completed",
                new SessionSummary
                {
                    TotalScripts = allResults.Count,
                    Succeeded = allResults.Count(r => r.Status == ExecutionStatus.Success),
                    Failed = allResults.Count(r => r.Status == ExecutionStatus.Failed),
                    Skipped = allResults.Count(r => r.Status == ExecutionStatus.Skipped),
                    ScriptsHandled = allResults.Select(r => r.Script.FileName).ToList()
                });
        }
        catch (OperationCanceledException)
        {
            StartSetLogger.Warning("Boot script execution cancelled");
            session.EndSession("cancelled", new SessionSummary());
        }
        catch (Exception ex)
        {
            StartSetLogger.Error(ex, "Boot script execution failed");
            session.EndSession("failed", new SessionSummary());
        }
        finally
        {
            StartSetLogger.SetSessionLogger(null);
        }

        // BootWorker completes after initial execution - doesn't need to run continuously
    }
}
