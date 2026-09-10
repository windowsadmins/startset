using System.Diagnostics;
using StartSet.Infrastructure.Logging;

namespace StartSet.Engine.Native;

/// <summary>
/// Decides when a user's desktop is actually there to be worked on.
///
/// Login payloads used to start the instant Security event 4624 was written.
/// That event is the authentication succeeding -- it fires before userinit runs,
/// before the shell is launched, and long before anything is on screen. The
/// payloads were therefore talking to a desktop that did not exist yet.
///
/// That is what made them hang, and it is the reason fixing them one at a time
/// never worked. A broadcast to every top-level window, a SendMessage to
/// Progman, Get-WinUserLanguageList reaching the shell through WinRT: none of
/// them have anything to answer while Explorer is still starting, so they wait,
/// and a wait with no shell on the other end does not end. One payload was still
/// blocked twenty-four minutes later on a lab machine, and the same payload
/// blocked again afterwards with a SendMessageTimeout already in it -- which
/// method it used was never the point.
///
/// outset has this right on the Mac: login-every work runs once the user is at
/// their desktop, not while the window server is still coming up. This is the
/// same rule for Windows.
///
/// The service lives in session 0, so it cannot see the user's windows -- window
/// stations are per-session and FindWindow would return nothing no matter how
/// ready the desktop was. What it can see across sessions is processes. So the
/// signal is Explorer running in the target session, plus a short settle after it
/// appears, because the shell accepts messages some moments after its process
/// starts.
///
/// This is a readiness gate, not a timeout. The per-payload and per-batch bounds
/// stay as safeguards for a script that is genuinely broken; this stops well
/// behaved scripts being asked to do the impossible.
/// </summary>
public static class ShellReadiness
{
    /// <summary>
    /// Waits until the shell is up for <paramref name="sessionId"/>, or until
    /// <paramref name="timeout"/> elapses.
    ///
    /// Returns true when the desktop is ready. Returns false on timeout, and the
    /// caller runs the payloads anyway: a session where Explorer never appears is
    /// unusual but it is not a reason to skip a user's configuration entirely, and
    /// the execution bounds still apply.
    /// </summary>
    public static async Task<bool> WaitForDesktopAsync(
        int sessionId,
        TimeSpan timeout,
        TimeSpan settle,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var started = DateTimeOffset.UtcNow;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
                return false;

            var shell = FindShellProcess(sessionId);
            if (shell is not null)
            {
                // Explorer's process exists a little before its message pump is
                // serving. Settling from the process's own start time rather than
                // from now means a shell that came up while we were waiting is not
                // penalised for it.
                var age = DateTimeOffset.UtcNow - shell.Value;
                if (age >= settle)
                {
                    StartSetLogger.Information(
                        "Desktop is up for session {Session} (shell running {Age:F0}s); starting login payloads after waiting {Waited:F0}s.",
                        sessionId, age.TotalSeconds, (DateTimeOffset.UtcNow - started).TotalSeconds);
                    return true;
                }

                var remaining = settle - age;
                await Task.Delay(Min(remaining, TimeSpan.FromSeconds(2)), cancellationToken).ConfigureAwait(false);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        StartSetLogger.Warning(
            "No shell appeared for session {Session} within {Timeout:F0}s. Running the login payloads anyway -- " +
            "the execution bounds still apply, but anything that needs the desktop may not be able to do its job.",
            sessionId, timeout.TotalSeconds);
        return false;
    }

    /// <summary>
    /// Start time of Explorer in the given session, or null if it is not running
    /// there yet.
    /// </summary>
    private static DateTimeOffset? FindShellProcess(int sessionId)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                using (p)
                {
                    if (p.SessionId != sessionId) continue;

                    try { return p.StartTime; }
                    catch { return DateTimeOffset.UtcNow; }  // running, start time unreadable
                }
            }
        }
        catch (Exception ex)
        {
            StartSetLogger.Debug("Could not enumerate the shell for session {Session}: {Error}", sessionId, ex.Message);
        }

        return null;
    }

    /// <summary>
    /// The session currently attached to the console, or -1 when none is.
    /// These are single-seat lab machines, so the console session is the user's
    /// session.
    /// </summary>
    public static int GetActiveConsoleSessionId()
    {
        try
        {
            var id = unchecked((int)WTSGetActiveConsoleSessionId());
            return id == -1 ? -1 : id;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// The user signed in to <paramref name="sessionId"/>, or null when nobody is.
    /// </summary>
    /// <remarks>
    /// Needed because a logon can be missed rather than observed. When the service
    /// starts after a user is already signed in there is no 4624 to read a username
    /// from, so it has to be asked of the session directly.
    ///
    /// An empty string means the session exists but has no user attached to it --
    /// the login screen. That is reported as null, not as a user named "".
    /// </remarks>
    public static string? GetSessionUserName(int sessionId)
    {
        if (sessionId < 0)
            return null;

        var buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsUserName, out buffer, out _))
                return null;

            var name = System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                try { WTSFreeMemory(buffer); } catch { }
            }
        }
    }

    private const int WtsUserName = 5;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
