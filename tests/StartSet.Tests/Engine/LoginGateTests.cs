using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using StartSet.Engine;
using StartSet.Engine.Native;
using Xunit;

namespace StartSet.Tests.Engine;

/// <summary>
/// Which logon events run the login payloads.
/// </summary>
/// <remarks>
/// Event 4624 with an interactive logon type is also written by scheduled tasks,
/// services calling LogonUser and runas, and twice for an administrator's own sign-in.
/// Each used to run the login batch against the console session, so a task that
/// logged on every minute ran the login payloads every minute.
/// </remarks>
public class LoginGateTests
{
    private static readonly DateTime SignedIn = new(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ALogonInSessionZeroIsNotASignIn()
    {
        Assert.NotNull(LoginGate.RejectBeforeWaiting(0));
    }

    [Fact]
    public void ALogonThatHasAlreadyEndedIsNotASignIn()
    {
        Assert.NotNull(LoginGate.RejectBeforeWaiting(LogonSessions.NoSuchLogonSession));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(-1)]
    public void ALogonInADesktopSessionOrAnUnknownOneGoesOnToTheSessionCheck(int session)
    {
        Assert.Null(LoginGate.RejectBeforeWaiting(session));
    }

    [Fact]
    public void TheFirstLogonOfASignInRunsTheBatch()
    {
        var gate = new LoginGate();

        Assert.Null(gate.Claim("alice", 1, "alice", SignedIn));
    }

    [Fact]
    public void LaterLogonsInTheSameSignInDoNotRunItAgain()
    {
        var gate = new LoginGate();
        gate.Claim("alice", 1, "alice", SignedIn);

        Assert.NotNull(gate.Claim("alice", 1, "alice", SignedIn));
        Assert.NotNull(gate.Claim(@"CONTOSO\Alice", 1, "alice", SignedIn));
    }

    [Fact]
    public void AnAdministratorsTwoTokensRunTheBatchOnce()
    {
        var gate = new LoginGate();

        var results = new[] { 0, 1 }.AsParallel()
            .Select(_ => gate.Claim("admin", 2, "admin", SignedIn))
            .ToArray();

        Assert.Single(results, r => r == null);
    }

    [Fact]
    public void ANewSignInToTheSameSessionRunsItAgain()
    {
        var gate = new LoginGate();
        gate.Claim("alice", 1, "alice", SignedIn);

        Assert.Null(gate.Claim("alice", 1, "alice", SignedIn.AddHours(2)));
        Assert.Null(gate.Claim("bob", 1, "bob", SignedIn.AddHours(3)));
    }

    [Fact]
    public void AnotherAccountLoggingOnInsideTheSessionIsNotASignIn()
    {
        var gate = new LoginGate();

        Assert.NotNull(gate.Claim("helpdesk", 1, "alice", SignedIn));
        Assert.Null(gate.Claim("alice", 1, "alice", SignedIn));
    }

    [Fact]
    public void ASessionNobodyIsSignedInToIsNotASignIn()
    {
        Assert.NotNull(new LoginGate().Claim("alice", 1, null, null));
    }

    [Fact]
    public void TheCatchUpNamesNoLogonUserAndIsStillDeduplicated()
    {
        var gate = new LoginGate();

        Assert.Null(gate.Claim(null, 1, "alice", SignedIn));
        Assert.NotNull(gate.Claim("alice", 1, "alice", SignedIn));
    }

    [Fact]
    public void ALogonSessionThatDoesNotExistIsReportedAsEnded()
    {
        var result = LogonSessions.GetSessionIdOfLogon(0x7FFFFFFF_FFFFFFF0);

        Assert.True(result == LogonSessions.NoSuchLogonSession || result == -1);
    }

    [Fact]
    public void ThisProcessesLogonSessionMapsToItsOwnSession()
    {
        var expected = Process.GetCurrentProcess().SessionId;

        Assert.Equal(expected, LogonSessions.GetSessionIdOfLogon(CurrentLogonId()));
    }

    [Fact]
    public void ASignedInDesktopSessionHasALogonTimeInThePast()
    {
        var session = Process.GetCurrentProcess().SessionId;
        if (session == 0) return;

        var logonTime = LogonSessions.GetSessionLogonTime(session);

        Assert.NotNull(logonTime);
        Assert.True(logonTime < DateTime.UtcNow);
    }

    [Fact]
    public void SessionZeroHasNoLogonTime()
    {
        Assert.Null(LogonSessions.GetSessionLogonTime(0));
    }

    private static ulong CurrentLogonId()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var size = Marshal.SizeOf<TokenStatistics>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Assert.True(GetTokenInformation(identity.Token, TokenStatisticsClass, buffer, size, out _));
            var stats = Marshal.PtrToStructure<TokenStatistics>(buffer);
            return ((ulong)(uint)stats.AuthenticationIdHigh << 32) | stats.AuthenticationIdLow;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int TokenStatisticsClass = 10;

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatistics
    {
        public uint TokenIdLow;
        public int TokenIdHigh;
        public uint AuthenticationIdLow;
        public int AuthenticationIdHigh;
        public long ExpirationTime;
        public int TokenType;
        public int ImpersonationLevel;
        public uint DynamicCharged;
        public uint DynamicAvailable;
        public uint GroupCount;
        public uint PrivilegeCount;
        public uint ModifiedIdLow;
        public int ModifiedIdHigh;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);
}
