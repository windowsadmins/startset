using StartSet.Core.Enums;
using StartSet.Core.Models;
using StartSet.Engine.Native;
using StartSet.Infrastructure.Logging;

namespace StartSet.Engine.Processors;

/// <summary>
/// The single place that decides whether a payload must run as the signed-in
/// user, and what happens when it cannot.
///
/// This lived inside the PowerShell processor, which meant .bat, .cmd and .exe
/// payloads dropped into login-every ran as SYSTEM in session 0 unconditionally
/// -- the same defect, with none of the mitigation. Whether a payload needs a
/// user session is a property of the payload type, not of the file extension, so
/// it belongs here rather than in each processor.
/// </summary>
internal static class UserContextExecution
{
    /// <summary>
    /// Payload types whose scripts are documented to run as the signed-in user.
    /// The *-privileged and boot-* types deliberately stay in the service's own
    /// SYSTEM context.
    /// </summary>
    public static bool RequiresUserContext(PayloadType type) => type switch
    {
        PayloadType.LoginOnce => true,
        PayloadType.LoginEvery => true,
        PayloadType.OnDemand => true,
        _ => false
    };

    /// <summary>
    /// Runs a user-context payload in the console user's session.
    ///
    /// Returns <c>null</c> when <paramref name="script"/> does not need a user
    /// session, in which case the caller proceeds with its own SYSTEM execution.
    /// Otherwise the returned result is final -- including a Deferred result when
    /// the session could not be reached.
    ///
    /// There is deliberately no path here that runs a user-context payload as
    /// SYSTEM. It cannot have its intended effect from session 0: HKCU writes land
    /// in SYSTEM's hive, user32 calls never reach the desktop, and a script that
    /// skips administrators matches SYSTEM and exits 0. That last case is the
    /// dangerous one, because it reports success.
    /// </summary>
    public static ExecutionResult? TryRunAsConsoleUser(
        ScriptPayload script,
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout,
        DateTimeOffset startTime)
    {
        if (!RequiresUserContext(script.PayloadType))
            return null;

        var asUser = UserSessionLauncher.Run(fileName, arguments, workingDirectory, timeout);

        if (asUser.Launched)
        {
            var result = new ExecutionResult
            {
                Script = script,
                Status = ExecutionStatus.Failed,
                StartTime = startTime,
                EndTime = DateTimeOffset.UtcNow,
                StandardOutput = asUser.StandardOutput,
                StandardError = asUser.StandardError
            };

            if (asUser.TimedOut)
            {
                result.Status = ExecutionStatus.Timeout;
                result.ErrorMessage = $"Script execution timed out after {timeout.TotalSeconds:F0} seconds";
                StartSetLogger.Warning("Script timed out in user session: {Script}", script.FileName);
                return result;
            }

            result.ExitCode = asUser.ExitCode;
            result.Status = asUser.ExitCode == 0 ? ExecutionStatus.Success : ExecutionStatus.Failed;

            if (result.Status == ExecutionStatus.Failed)
            {
                result.ErrorMessage = $"Exit code: {asUser.ExitCode}";
                StartSetLogger.Warning("Script failed in user session with exit code {ExitCode}: {Script}",
                    asUser.ExitCode, script.FileName);
            }
            else
            {
                StartSetLogger.Information("Script completed successfully in user session: {Script}", script.FileName);
            }

            return result;
        }

        // Not run. See the class comment for why there is no SYSTEM fallback.
        var reason = asUser.FailureReason ?? "unknown";
        StartSetLogger.Warning(
            "Deferred {Script}: could not reach the console user's session ({Reason}). " +
            "The script did NOT run. It is user-context, so running it as SYSTEM would not have applied it either.",
            script.FileName, reason);

        var deferred = ExecutionResult.Deferred(
            script,
            $"Not run: could not start in the console user's session ({reason})");
        deferred.StartTime = startTime;
        deferred.EndTime = DateTimeOffset.UtcNow;
        return deferred;
    }
}
