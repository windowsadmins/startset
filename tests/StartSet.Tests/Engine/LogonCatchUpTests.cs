using StartSet.Core.Models;
using StartSet.Engine.Native;
using Xunit;

namespace StartSet.Tests.Engine;

/// <summary>
/// The start-up catch-up for a logon that happened before the event subscription existed.
/// </summary>
/// <remarks>
/// EventLogWatcher delivers only what is written after it is enabled, so a logon during
/// service start-up is not late - it never arrives. On a machine configured for automatic
/// logon that is every boot: measured on a laser workstation, the service process started
/// at 15:47:36.214 and the autologon wrote its 4624 at 15:47:36.996. The login batch never
/// ran, so the desktop had none of its customizations and the laser software was never
/// signed in.
/// </remarks>
public class LogonCatchUpTests
{
    [Fact]
    public void GraceDefaultsToLongerThanTheRaceItCovers()
    {
        var prefs = new StartSetPreferences();

        // The observed gap was under a second. The default has to clear that by enough
        // that a real event still wins the race and the catch-up stays the fallback.
        Assert.True(prefs.LogonCatchUpGrace >= 10,
            "a grace shorter than the logon race would fire the catch-up while the event is still in flight");

        // And short enough that a user already at the desktop is not left waiting.
        Assert.True(prefs.LogonCatchUpGrace <= 60,
            "a long grace delays every automatic-logon machine's customizations");
    }

    [Fact]
    public void GraceIsConfigurable()
    {
        var prefs = new StartSetPreferences { LogonCatchUpGrace = 45 };

        Assert.Equal(45, prefs.LogonCatchUpGrace);
    }

    [Fact]
    public void SessionUserNameIsNullForANonSession()
    {
        // A negative id is what GetActiveConsoleSessionId returns when nothing is
        // attached to the console. Asking Windows about it must not throw - the
        // catch-up runs on every service start, including at the login screen.
        Assert.Null(ShellReadiness.GetSessionUserName(-1));
    }

    [Fact]
    public void SessionUserNameIsNullForSessionZero()
    {
        // Session 0 is the service session. It has no interactive user, and reporting
        // one would make the catch-up run login payloads against no desktop.
        Assert.Null(ShellReadiness.GetSessionUserName(0));
    }
}
