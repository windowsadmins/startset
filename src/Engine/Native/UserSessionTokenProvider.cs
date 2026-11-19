using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StartSet.Infrastructure.Logging;

namespace StartSet.Engine.Native;

/// <summary>Acquires a token without switching away from the requested desktop.</summary>
internal sealed class UserSessionTokenProvider(
    Func<int> getConsoleSessionId,
    UserSessionTokenProvider.QueryToken queryToken,
    TimeSpan tokenWait,
    TimeSpan pollInterval)
{
    internal delegate bool QueryToken(int sessionId, out SafeAccessTokenHandle token);

    // A failed console session must not suppress the wait for an RDP session.
    private readonly ConcurrentDictionary<int, byte> _exhausted = new();

    public bool TryGetToken(int? targetSessionId, out SafeAccessTokenHandle token, out string failureReason)
    {
        // Resolve once: neither a console switch nor a retry may redirect the payload.
        var sessionId = targetSessionId ?? getConsoleSessionId();
        token = null!;
        if (sessionId <= 0)
        {
            failureReason = $"No interactive user session selected (session {sessionId})";
            return false;
        }

        var wait = _exhausted.ContainsKey(sessionId) ? TimeSpan.Zero : tokenWait;
        var timer = Stopwatch.StartNew();
        var attempts = 0;

        while (true)
        {
            attempts++;
            if (queryToken(sessionId, out var candidate))
            {
                _exhausted.TryRemove(sessionId, out _);
                if (attempts > 1)
                {
                    StartSetLogger.Information(
                        "User token became available for session {SessionId} after {Attempts} attempts.",
                        sessionId, attempts);
                }

                token = candidate;
                failureReason = string.Empty;
                return true;
            }

            var error = Marshal.GetLastWin32Error();
            candidate?.Dispose();
            failureReason = $"WTSQueryUserToken failed for session {sessionId}: {new Win32Exception(error).Message}";
            if (timer.Elapsed >= wait)
            {
                if (wait > TimeSpan.Zero)
                {
                    _exhausted.TryAdd(sessionId, 0);
                    failureReason += $" (still unavailable after {wait.TotalSeconds:F0}s, {attempts} attempts)";
                }
                else
                {
                    failureReason += " (not waiting again for this session)";
                }
                return false;
            }

            Thread.Sleep(pollInterval);
        }
    }
}
