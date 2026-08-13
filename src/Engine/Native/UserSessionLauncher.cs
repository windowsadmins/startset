using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> as the
    /// console user. Returns Launched=false (with FailureReason set) when no user
    /// session is available or the token could not be obtained, so the caller can
    /// fall back rather than losing the execution entirely.
    /// </summary>
    public static LaunchResult Run(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
            return Fail("no active console session");

        if (!WTSQueryUserToken(sessionId, out var userToken))
            return Fail($"WTSQueryUserToken failed for session {sessionId}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

        using (userToken)
        {
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
                if (!CreateEnvironmentBlock(out envBlock, primaryToken, false))
                    envBlock = IntPtr.Zero;

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
            var stdoutTask = ReadAllAsync(outRead);
            var stderrTask = ReadAllAsync(errRead);

            var waitMs = timeout == Timeout.InfiniteTimeSpan
                ? INFINITE
                : (uint)Math.Max(0, timeout.TotalMilliseconds);

            var waited = WaitForSingleObject(pi.hProcess, waitMs);
            var timedOut = waited == WAIT_TIMEOUT;

            if (timedOut)
            {
                try { TerminateProcess(pi.hProcess, 1); } catch { }
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

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

    private static async Task<string> ReadAllAsync(SafeFileHandle handle)
    {
        try
        {
            await using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static LaunchResult Fail(string reason) =>
        new(Launched: false, ExitCode: -1, StandardOutput: "", StandardError: "", TimedOut: false, FailureReason: reason);

    // ── interop ─────────────────────────────────────────────────────────────

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
