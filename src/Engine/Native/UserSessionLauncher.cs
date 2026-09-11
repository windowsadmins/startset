using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StartSet.Infrastructure.Logging;

namespace StartSet.Engine.Native;

/// <summary>
/// Launches a process in the interactive console session, running as the user who
/// is signed in there, and captures its output.
///
/// StartSet runs as a LocalSystem service, so a plain Process.Start() child also
/// runs as SYSTEM in session 0. That is correct for boot-* and *-privileged
/// payloads, but wrong for the login-* and on-demand types, which are documented
/// as user context: their scripts write HKCU and call user32 APIs such as
/// SystemParametersInfo, neither of which reaches the signed-in user from
/// session 0. Scripts that guard on "skip administrators" also match SYSTEM and
/// silently exit 0.
///
/// The sequence is the standard one for a service launching into a user session:
/// find the active console session, borrow its user token, build that user's
/// environment, and CreateProcessAsUser onto their desktop.
/// </summary>
public static class UserSessionLauncher
{
    public sealed record LaunchResult(
        bool Launched,
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool TimedOut,
        string? FailureReason);

    /// <summary>
    /// True when there is an interactive console session with a user signed in.
    /// Callers use this to decide whether user-context execution is possible at
    /// all before attempting it.
    /// </summary>
    public static bool HasInteractiveUser()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return false;
        if (!WTSQueryUserToken(sessionId, out var token)) return false;
        token.Dispose();
        return true;
    }

    /// <summary>
    /// How long to keep trying for the console user's token before giving up.
    ///
    /// At sign-in the session exists before a user token is available for it, and
    /// StartSet's login triggers fire inside that window. A single attempt at the
    /// exact moment of logon is the thinnest possible sampling of a race, and the
    /// observed failure -- "a token that does not exist" -- is a timing condition,
    /// not a permanent one.
    ///
    /// The cost is paid at most once per run: as soon as the token exists, every
    /// later script in the same pass gets it on the first attempt.
    /// </summary>
    private static readonly TimeSpan TokenWait = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan TokenPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Set once this process has waited out the full <see cref="TokenWait"/> without
    /// ever seeing a token.
    ///
    /// Without this the wait is paid per script rather than per run. StartSet
    /// executes payloads sequentially, so on a machine where impersonation is
    /// genuinely unavailable a dozen payloads would each block for the full
    /// minute -- turning a one-minute wait into a quarter-hour of login. Once the
    /// first script has established that nothing is coming, the rest still make a
    /// single attempt each (a session appearing late is still picked up) but no
    /// longer wait for it.
    ///
    /// Static, so the memo lasts exactly one run of the process, which is the
    /// scope this belongs at.
    /// </summary>
    private static bool _tokenWaitExhausted;

    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> as the
    /// console user. Returns Launched=false (with FailureReason set) when no user
    /// session is available or the token could not be obtained.
    ///
    /// A false result means the payload did not run. Callers must not treat it as
    /// licence to run the same script as SYSTEM: a user-context payload executed
    /// in session 0 cannot have its intended effect, and reporting that as success
    /// is what kept this failure invisible.
    /// </summary>
    public static LaunchResult Run(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        // Each native step is timed and logged.
        //
        // When this sequence blocks there is nothing to see from outside: the engine
        // logs "Executing <script>", the launch never returns, and the per-script
        // timeout reports "Script timed out in user session" -- which reads as though
        // the script ran and hung. It did not. On a lab workstation a payload that
        // only printed three lines and exited timed out identically, and no child
        // process was ever created, so the block was somewhere in here rather than in
        // any script. Four candidate calls and no way to tell which.
        //
        // These are Debug, so they cost nothing normally and name the exact call the
        // next time it happens.
        var launchTimer = System.Diagnostics.Stopwatch.StartNew();

        void Step(string call) =>
            StartSetLogger.Debug("User-session launch: {Call} at {Elapsed:N1}s", call, launchTimer.Elapsed.TotalSeconds);

        Step("acquiring console user token");

        if (!TryGetConsoleUserToken(out var userToken, out var failure))
            return Fail(failure);

        using (userToken)
        {
            Step("DuplicateTokenEx");

            if (!DuplicateTokenEx(
                    userToken,
                    TOKEN_ALL_ACCESS,
                    IntPtr.Zero,
                    SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    TOKEN_TYPE.TokenPrimary,
                    out var primaryToken))
            {
                return Fail($"DuplicateTokenEx failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }

            using (primaryToken)
            {
                var envBlock = IntPtr.Zero;
                // Without the user's environment block the child inherits none of
                // their profile paths -- USERPROFILE, APPDATA and LOCALAPPDATA all
                // matter to login scripts.
                //
                // This one talks to the User Profile Service, so it is the most
                // likely of the four to block on a machine carrying hundreds of
                // stale profiles.
                Step("CreateEnvironmentBlock");

                if (!CreateEnvironmentBlock(out envBlock, primaryToken, false))
                    envBlock = IntPtr.Zero;

                Step("CreateProcessAsUser");

                try
                {
                    return Launch(primaryToken, envBlock, fileName, arguments, workingDirectory, timeout);
                }
                finally
                {
                    if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
                }
            }
        }
    }

    /// <summary>
    /// Waits for a console session that has a user token, up to <see cref="TokenWait"/>.
    /// </summary>
    private static bool TryGetConsoleUserToken(
        out SafeAccessTokenHandle token,
        out string failureReason)
    {
        var wait = _tokenWaitExhausted ? TimeSpan.Zero : TokenWait;
        var deadline = DateTime.UtcNow + wait;
        var attempts = 0;
        var lastReason = "no active console session";

        while (true)
        {
            attempts++;

            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF)
            {
                lastReason = "no active console session";
            }
            else if (WTSQueryUserToken(sessionId, out var candidate))
            {
                if (attempts > 1)
                {
                    StartSetLogger.Information(
                        "Console user token became available for session {SessionId} after {Attempts} attempts.",
                        sessionId, attempts);
                }

                token = candidate;
                failureReason = string.Empty;
                return true;
            }
            else
            {
                lastReason = $"WTSQueryUserToken failed for session {sessionId}: " +
                             $"{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            }

            if (DateTime.UtcNow >= deadline)
            {
                token = null!;
                if (wait > TimeSpan.Zero)
                {
                    _tokenWaitExhausted = true;
                    failureReason = attempts == 1
                        ? lastReason
                        : $"{lastReason} (still unavailable after {TokenWait.TotalSeconds:F0}s, {attempts} attempts)";
                }
                else
                {
                    failureReason = $"{lastReason} (not waiting again; " +
                                    $"an earlier payload in this run already waited {TokenWait.TotalSeconds:F0}s)";
                }
                return false;
            }

            Thread.Sleep(TokenPollInterval);
        }
    }

    private static LaunchResult Launch(
        SafeAccessTokenHandle token,
        IntPtr envBlock,
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };

        if (!CreatePipe(out var outRead, out var outWrite, ref sa, 0))
            return Fail("CreatePipe(stdout) failed");
        if (!CreatePipe(out var errRead, out var errWrite, ref sa, 0))
        {
            outRead.Dispose(); outWrite.Dispose();
            return Fail("CreatePipe(stderr) failed");
        }

        // The read ends stay with us; they must not be inherited or the child
        // holds a copy and the pipe never reaches EOF.
        SetHandleInformation(outRead, HANDLE_FLAG_INHERIT, 0);
        SetHandleInformation(errRead, HANDLE_FLAG_INHERIT, 0);

        var si = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            // Without the desktop the process starts but cannot interact with the
            // session; user32 calls fail in ways that look like success.
            lpDesktop = @"winsta0\default",
            dwFlags = STARTF_USESTDHANDLES,
            hStdOutput = outWrite.DangerousGetHandle(),
            hStdError = errWrite.DangerousGetHandle(),
            hStdInput = IntPtr.Zero
        };

        // CreateProcessAsUser mutates the command line buffer, so it cannot be a
        // literal string.
        var commandLine = new StringBuilder($"\"{fileName}\" {arguments}");

        var flags = CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT;

        var created = CreateProcessAsUser(
            token,
            null,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            true,
            flags,
            envBlock,
            workingDirectory,
            ref si,
            out var pi);

        var lastError = Marshal.GetLastWin32Error();

        // Close our copies of the write ends immediately: while the parent holds
        // one, reading the corresponding pipe blocks forever after the child exits.
        outWrite.Dispose();
        errWrite.Dispose();

        if (!created)
        {
            outRead.Dispose();
            errRead.Dispose();
            return Fail($"CreateProcessAsUser failed: {new Win32Exception(lastError).Message}");
        }

        try
        {
            var stdoutSink = new StringBuilder();
            var stderrSink = new StringBuilder();

            var stdoutTask = ReadAllAsync(outRead, stdoutSink);
            var stderrTask = ReadAllAsync(errRead, stderrSink);

            var waitMs = timeout == Timeout.InfiniteTimeSpan
                ? INFINITE
                : (uint)Math.Max(0, timeout.TotalMilliseconds);

            var waited = WaitForSingleObject(pi.hProcess, waitMs);
            var timedOut = waited == WAIT_TIMEOUT;

            if (timedOut)
            {
                try { TerminateProcess(pi.hProcess, 1); } catch { }
            }

            // The script has exited. Its OUTPUT may not have finished, and waiting
            // for it unbounded is a deadlock with no way out.
            //
            // The write ends of these pipes are inheritable, and CreateProcessAsUser
            // is called with inheritHandles: true, so every descendant gets a copy --
            // not just the script. A payload whose job is to start something and
            // leave it running (a tray app, a browser, anything with a window) leaves
            // that grandchild holding the write end after the script itself exits.
            // The pipe therefore never reaches EOF, and the read blocks forever.
            //
            // Closing our own copies above is not enough; that only covers the
            // parent. This is the case it misses.
            //
            // Measured 2026-09-09 on a lab workstation: a login payload started a
            // GUI helper, exited, and the helper kept the pipe open. The read never
            // returned, and because the engine runs payloads sequentially the ENTIRE
            // login batch stopped at the first script -- no taskbar, no wallpaper,
            // no laser window -- with nothing logged as a failure. It presented to
            // the technician as a frozen machine that had to be power-cycled.
            //
            // So the drain gets a grace period of its own. Whatever has arrived is
            // kept; anything still outstanding is abandoned and the batch moves on.
            // A payload must never be able to wedge a user's session.
            var drained = Task.WhenAll(stdoutTask, stderrTask).Wait(OutputDrainGrace);

            if (!drained)
            {
                // Disposing the read ends makes the abandoned readers fault out of
                // their pending ReadAsync rather than linger for the life of the
                // service.
                outRead.Dispose();
                errRead.Dispose();
            }

            string stdout, stderr;
            lock (stdoutSink) { stdout = stdoutSink.ToString(); }
            lock (stderrSink) { stderr = stderrSink.ToString(); }

            if (!drained)
            {
                stderr = string.IsNullOrEmpty(stderr)
                    ? OutputAbandonedNote
                    : stderr.TrimEnd() + Environment.NewLine + OutputAbandonedNote;
            }

            uint exitCode = 0;
            if (!timedOut) GetExitCodeProcess(pi.hProcess, out exitCode);

            return new LaunchResult(
                Launched: true,
                ExitCode: (int)exitCode,
                StandardOutput: stdout.TrimEnd(),
                StandardError: stderr.TrimEnd(),
                TimedOut: timedOut,
                FailureReason: null);
        }
        finally
        {
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
        }
    }

    /// <summary>
    /// Drains a pipe into <paramref name="sink"/> as the bytes arrive.
    ///
    /// Deliberately incremental rather than ReadToEndAsync. When a payload leaves
    /// a child running, this task never finishes, and the caller abandons it --
    /// at which point ReadToEndAsync would have returned nothing at all and the
    /// script's output would be lost from the log. Appending as we go means the
    /// caller keeps whatever the script actually managed to write.
    /// </summary>
    private static async Task ReadAllAsync(SafeFileHandle handle, StringBuilder sink)
    {
        try
        {
            await using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var buffer = new char[1024];
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                lock (sink) { sink.Append(buffer, 0, read); }
            }
        }
        catch
        {
            // Includes the handle being disposed out from under us, which is how
            // the caller unblocks an abandoned read. Whatever reached the sink
            // before that stands.
        }
    }

    private static LaunchResult Fail(string reason) =>
        new(Launched: false, ExitCode: -1, StandardOutput: "", StandardError: "", TimedOut: false, FailureReason: reason);

    // ── interop ─────────────────────────────────────────────────────────────

    /// <summary>
    /// How long to keep draining a payload's output after the payload itself has
    /// exited. Generous enough that ordinary buffered output is never truncated,
    /// short enough that a payload which leaves a child holding the pipe costs
    /// seconds rather than the whole login batch.
    /// </summary>
    private static readonly TimeSpan OutputDrainGrace = TimeSpan.FromSeconds(10);

    private const string OutputAbandonedNote =
        "[startset] the script exited but something it started still holds the output pipe, " +
        "so the remaining output was abandoned after the drain grace period. This is not a " +
        "failure of the script; it is how a payload that launches a long-lived child behaves.";

    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint STARTF_USESTDHANDLES = 0x00000100;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_TIMEOUT = 0x00000102;

    private enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }
    private enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel,
        TOKEN_TYPE tokenType,
        out SafeAccessTokenHandle newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        SafeAccessTokenHandle token,
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, ref SECURITY_ATTRIBUTES attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
