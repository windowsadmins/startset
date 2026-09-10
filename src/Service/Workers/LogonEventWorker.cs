using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using StartSet.Core.Enums;
using StartSet.Engine;
using StartSet.Infrastructure.Configuration;
using StartSet.Engine.Native;
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

            // A logon that happened before the watcher existed is never delivered.
            await RunForAlreadySignedInUserAsync(stoppingToken);

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
            // Query for interactive logon events.
            //   2  Interactive         - console logon, uncached credentials
            //   10 RemoteInteractive   - RDP
            //   11 CachedInteractive   - console logon against cached credentials
            //
            // 11 is the common case on domain-joined and Entra-joined machines: the
            // FIRST logon for a user is type 2, and every logon after that is type 11
            // once their credentials are cached. Watching only 2 and 10 meant login
            // payloads ran once per user per machine and then silently never again,
            // which is invisible in the logs because system pseudo-sessions keep
            // producing type 2 events (see IsSystemAccount below).
            var query = new EventLogQuery(
                "Security",
                PathType.LogName,
                $"*[System[EventID={EventIdLogon}]] and *[EventData[Data[@Name='LogonType']='2' or Data[@Name='LogonType']='10' or Data[@Name='LogonType']='11']]");

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

            // Tells the start-up catch-up that the watcher is delivering events and it
            // should stand down. Set before the desktop wait rather than after: that
            // wait can take a minute, and the catch-up must not fire in the meantime.
            Volatile.Write(ref _observedLogon, 1);

            var sessionId = GetSessionIdForLogon(record);
            await RunLoginPayloadsAsync(username, sessionId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StartSetLogger.Error(ex, "Error processing logon event");
        }
    }

    /// <summary>
    /// Runs the login payload batch for <paramref name="username"/> in
    /// <paramref name="sessionId"/>.
    /// </summary>
    /// <remarks>
    /// Shared by the two ways a logon reaches this worker: the event watcher, and the
    /// start-up catch-up for a session that was already signed in. They differ only in
    /// how the user was discovered, so one body means a fix to the batch cannot land on
    /// one path and miss the other.
    /// </remarks>
    private async Task RunLoginPayloadsAsync(string? username, int sessionId, CancellationToken stoppingToken)
    {
        try
        {
            // Wait for the user's desktop before touching it.
            //
            // 4624 is the authentication succeeding, not the desktop appearing.
            // Starting payloads here means starting them before userinit has run
            // and before the shell exists, and a payload that asks the shell for
            // something then simply waits -- which is how a login batch came to
            // sit blocked for twenty-four minutes on a lab machine. See
            // ShellReadiness for the full account.
            var prefs = _preferencesService.Preferences;

            if (sessionId >= 0)
            {
                await ShellReadiness.WaitForDesktopAsync(
                    sessionId,
                    TimeSpan.FromSeconds(prefs.ShellReadyTimeout),
                    TimeSpan.FromSeconds(prefs.ShellSettleDelay),
                    CancellationToken.None);
            }
            else
            {
                StartSetLogger.Warning(
                    "Could not determine the session for this logon, so the desktop readiness wait was skipped. " +
                    "Payloads may run before the shell is up.");
            }

            // Any additional configured delay is applied on top.
            var delay = prefs.LoginDelay;
            if (delay > 0)
            {
                StartSetLogger.Debug("Waiting a further {Delay}s before running login scripts", delay);
                await Task.Delay(TimeSpan.FromSeconds(delay));
            }

            using var session = new SessionLogger();
            session.StartSession("login");
            StartSetLogger.SetSessionLogger(session);

            try
            {
                // Execute login scripts
                var engine = new ExecutionEngine(_preferencesService);

                // Run user-context login scripts
                var results = await engine.ExecuteAsync(
                    [PayloadType.LoginOnce, PayloadType.LoginEvery],
                    username: username,
                    waitForNetwork: false);

                // Always run login-privileged-every scripts (elevated, every login)
                var privEveryResults = await engine.ExecuteAsync(
                    [PayloadType.LoginPrivilegedEvery],
                    username: username,
                    waitForNetwork: false);
                results = results.Concat(privEveryResults).ToList();

                // Check for login-privileged trigger (gates once scripts only)
                if (File.Exists(StartSet.Core.Constants.Paths.TriggerLoginPrivileged))
                {
                    var privOnceResults = await engine.ExecuteAsync(
                        [PayloadType.LoginPrivilegedOnce],
                        username: username,
                        waitForNetwork: false);

                    results = results.Concat(privOnceResults).ToList();

                    try { File.Delete(StartSet.Core.Constants.Paths.TriggerLoginPrivileged); } catch { }
                }

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
            }
            finally
            {
                StartSetLogger.SetSessionLogger(null);
            }
        }
        catch (Exception ex)
        {
            StartSetLogger.Error(ex, "Error running login payloads");
        }
    }

    private int _observedLogon;

    /// <summary>
    /// Runs the login payloads for a user who was already signed in when the service
    /// started, when no logon event arrived to do it.
    /// </summary>
    /// <remarks>
    /// EventLogWatcher only ever delivers events written after it is enabled. Anything
    /// earlier is not queued, not replayed, and not visible -- it is simply gone.
    ///
    /// On a workstation configured for automatic logon that is the normal case, not an
    /// edge case. The console logon happens within a second or two of the service
    /// starting, landing in the window between the process starting and the
    /// subscription going live. Measured on a laser workstation: the service process
    /// started at 15:47:36.214 and the autologon wrote its 4624 at 15:47:36.996. The
    /// login batch never ran, so the desktop the user sat down at had none of its
    /// customizations and the laser software was never signed in -- every boot, on a
    /// machine whose whole purpose is to log itself in.
    ///
    /// Deliberately a grace period rather than bookkeeping about which logons have been
    /// handled. The race is a second or two wide, so waiting briefly and then asking
    /// "did an event arrive?" settles it without having to identify individual logon
    /// sessions. If the watcher delivered anything, this stands down.
    ///
    /// Running twice is the acceptable failure here and running never is not:
    /// login-every payloads are idempotent by contract and login-once is gated by its
    /// own run-once record, so a duplicate costs seconds. That asymmetry is why this
    /// errs toward running.
    /// </remarks>
    private async Task RunForAlreadySignedInUserAsync(CancellationToken stoppingToken)
    {
        try
        {
            var prefs = _preferencesService.Preferences;
            var grace = TimeSpan.FromSeconds(Math.Max(1, prefs.LogonCatchUpGrace));

            await Task.Delay(grace, stoppingToken);

            if (Volatile.Read(ref _observedLogon) != 0)
            {
                StartSetLogger.Debug("A logon event arrived during start-up; the catch-up is not needed.");
                return;
            }

            var sessionId = ShellReadiness.GetActiveConsoleSessionId();
            if (sessionId < 0)
            {
                StartSetLogger.Debug("No console session attached at start-up; nothing to catch up.");
                return;
            }

            var username = ShellReadiness.GetSessionUserName(sessionId);
            if (string.IsNullOrWhiteSpace(username) || IsSystemAccount(username))
            {
                StartSetLogger.Debug("Console session {Session} has no interactive user at start-up; nothing to catch up.", sessionId);
                return;
            }

            StartSetLogger.Information(
                "{User} was already signed in to session {Session} when the service started and no logon event " +
                "arrived within {Grace}s, so the login payloads are running now. This is the automatic-logon case: " +
                "the logon precedes the event subscription, so the event is never delivered.",
                username, sessionId, grace.TotalSeconds);

            Volatile.Write(ref _observedLogon, 1);

            await RunLoginPayloadsAsync(username, sessionId, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Service is stopping.
        }
        catch (Exception ex)
        {
            // Never fatal: the watcher remains the primary path.
            StartSetLogger.Error(ex, "The start-up login catch-up failed");
        }
    }

    /// <summary>
    /// Session the logon belongs to. Event 4624 does not carry a session id, but
    /// TargetLogonId identifies the logon session, and that maps to the Terminal
    /// Services session the shell will start in. Falls back to the active console
    /// session, which is the right answer on these single-seat machines.
    /// </summary>
    private static int GetSessionIdForLogon(System.Diagnostics.Eventing.Reader.EventRecord record)
    {
        try
        {
            var sessionValue = GetEventDataValue(record, "SessionId");
            if (!string.IsNullOrWhiteSpace(sessionValue) && int.TryParse(sessionValue, out var parsed))
                return parsed;
        }
        catch { }

        try
        {
            var console = (int)WTSGetActiveConsoleSessionId();
            // 0xFFFFFFFF means no session is attached to the console right now.
            return console == -1 ? -1 : console;
        }
        catch
        {
            return -1;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

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

    // Desktop Window Manager and User Mode Font Driver accounts are named
    // DWM-<n> / UMFD-<n>, where <n> is the session id and grows without bound on
    // a machine that accumulates sessions. A fixed list up to 3 let DWM-4 and
    // above through on shared machines, which ran a full login cycle for a
    // pseudo-session and wrote per-user state for an account that is not a user.
    private static readonly Regex SessionPseudoAccount =
        new(@"^(DWM|UMFD)-\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsSystemAccount(string? username)
    {
        if (string.IsNullOrEmpty(username)) return true;

        var systemAccounts = new[]
        {
            "SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE",
            "ANONYMOUS LOGON"
        };

        return systemAccounts.Contains(username.ToUpperInvariant()) ||
               SessionPseudoAccount.IsMatch(username) ||
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
