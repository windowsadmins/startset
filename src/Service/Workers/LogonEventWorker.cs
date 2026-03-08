using System.Diagnostics.Eventing.Reader;
using Microsoft.Extensions.Hosting;
using StartSet.Core.Enums;
using StartSet.Engine;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;

namespace StartSet.Service.Workers;

/// <summary>
/// Worker that monitors Windows Event Log for user logon events.
/// </summary>
public class LogonEventWorker : BackgroundService
{
    private readonly PreferencesService _preferencesService;
    private EventLogWatcher? _watcher;

    // Windows Security Event IDs
    private const int EventIdLogon = 4624;           // Successful logon
    private const int EventIdInteractiveLogon = 4648; // Explicit credentials logon

    public LogonEventWorker(PreferencesService preferencesService)
    {
        _preferencesService = preferencesService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartSetLogger.Information("LogonEventWorker starting");

        try
        {
            SetupEventLogWatcher();

            // Keep running until cancelled
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            StartSetLogger.Error(ex, "LogonEventWorker failed");
        }
    }

    private void SetupEventLogWatcher()
    {
        try
        {
            // Query for interactive logon events (LogonType 2 or 10 for interactive/RDP)
            // Event 4624 with LogonType 2 (Interactive) or 10 (RemoteInteractive/RDP)
            var query = new EventLogQuery(
                "Security",
                PathType.LogName,
                $"*[System[EventID={EventIdLogon}]] and *[EventData[Data[@Name='LogonType']='2' or Data[@Name='LogonType']='10']]");

            _watcher = new EventLogWatcher(query);
            _watcher.EventRecordWritten += OnLogonEvent;
            _watcher.Enabled = true;

            StartSetLogger.Information("Event log watcher started for logon events");
        }
        catch (UnauthorizedAccessException)
        {
            StartSetLogger.Warning("Insufficient permissions to monitor Security event log. Login scripts will not be triggered automatically.");
        }
        catch (Exception ex)
        {
            StartSetLogger.Error(ex, "Failed to set up event log watcher");
        }
    }

    private async void OnLogonEvent(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventRecord == null) return;

        try
        {
            var record = e.EventRecord;
            var username = GetEventDataValue(record, "TargetUserName");
            var domain = GetEventDataValue(record, "TargetDomainName");
            var logonType = GetEventDataValue(record, "LogonType");

            // Skip system accounts
            if (IsSystemAccount(username))
                return;

            var fullUsername = string.IsNullOrEmpty(domain) ? username : $"{domain}\\{username}";
            StartSetLogger.Information("Logon detected for user: {User} (LogonType: {Type})", fullUsername ?? "Unknown", logonType ?? "Unknown");

            // Add configurable delay if set
            var delay = _preferencesService.Preferences.LoginDelay;
            if (delay > 0)
            {
                StartSetLogger.Debug("Waiting {Delay}s before running login scripts", delay);
                await Task.Delay(TimeSpan.FromSeconds(delay));
            }

            // Execute login scripts
            var engine = new ExecutionEngine(_preferencesService);
            
            // Run user-context login scripts
            await engine.ExecuteAsync(
                [PayloadType.LoginOnce, PayloadType.LoginEvery],
                username: username,
                waitForNetwork: false);

            // Check for login-privileged trigger
            if (File.Exists(StartSet.Core.Constants.Paths.TriggerLoginPrivileged))
            {
                await engine.ExecuteAsync(
                    [PayloadType.LoginPrivilegedOnce, PayloadType.LoginPrivilegedEvery],
                    username: username,
                    waitForNetwork: false);

                try { File.Delete(StartSet.Core.Constants.Paths.TriggerLoginPrivileged); } catch { }
            }
        }
        catch (Exception ex)
        {
            StartSetLogger.Error(ex, "Error processing logon event");
        }
    }

    private static string? GetEventDataValue(EventRecord record, string name)
    {
        try
        {
            var properties = ((EventLogRecord)record).Properties;
            // Event 4624 property order: SubjectUserSid, SubjectUserName, SubjectDomainName, SubjectLogonId,
            // TargetUserSid, TargetUserName, TargetDomainName, TargetLogonId, LogonType...
            return name switch
            {
                "TargetUserName" => properties.Count > 5 ? properties[5].Value?.ToString() : null,
                "TargetDomainName" => properties.Count > 6 ? properties[6].Value?.ToString() : null,
                "LogonType" => properties.Count > 8 ? properties[8].Value?.ToString() : null,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSystemAccount(string? username)
    {
        if (string.IsNullOrEmpty(username)) return true;

        var systemAccounts = new[]
        {
            "SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE",
            "ANONYMOUS LOGON", "DWM-1", "DWM-2", "DWM-3",
            "UMFD-0", "UMFD-1", "UMFD-2", "UMFD-3"
        };

        return systemAccounts.Contains(username.ToUpperInvariant()) ||
               username.EndsWith("$"); // Computer accounts end with $
    }

    public override void Dispose()
    {
        if (_watcher != null)
        {
            _watcher.Enabled = false;
            _watcher.Dispose();
        }
        base.Dispose();
    }
}
