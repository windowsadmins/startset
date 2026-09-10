using FluentAssertions;
using StartSet.Core.Models;

namespace StartSet.Tests.Core;

public class StartSetPreferencesTests
{
    [Fact]
    public void Default_ReturnsNewInstance()
    {
        var a = StartSetPreferences.Default;
        var b = StartSetPreferences.Default;

        // Default should return new instances (not mutable singletons)
        a.Should().NotBeSameAs(b);
    }

    [Fact]
    public void Default_HasExpectedValues()
    {
        var prefs = StartSetPreferences.Default;

        prefs.WaitForNetwork.Should().BeTrue();
        prefs.NetworkTimeout.Should().Be(180);
        prefs.IgnoreNetworkFailure.Should().BeFalse();
        prefs.Verbose.Should().BeFalse();
        prefs.Debug.Should().BeFalse();
        prefs.ScriptTimeout.Should().Be(3600);
        prefs.ParallelExecution.Should().BeFalse();
        prefs.LoginDelay.Should().Be(0);
        prefs.LogScriptOutput.Should().BeTrue();
        prefs.ChecksumValidation.Should().BeFalse();
    }

    [Fact]
    public void Default_AllowedExtensions_ContainsStandardTypes()
    {
        var prefs = StartSetPreferences.Default;

        prefs.AllowedExtensions.Should().Contain(".ps1");
        prefs.AllowedExtensions.Should().Contain(".cmd");
        prefs.AllowedExtensions.Should().Contain(".bat");
        prefs.AllowedExtensions.Should().Contain(".exe");
        prefs.AllowedExtensions.Should().Contain(".msi");
    }

    [Fact]
    public void Default_IgnoredUsers_IsEmpty()
    {
        StartSetPreferences.Default.IgnoredUsers.Should().BeEmpty();
    }

    [Fact]
    public void Default_Overrides_IsEmpty()
    {
        StartSetPreferences.Default.Overrides.Should().BeEmpty();
    }

    // ──────────── login payload timeout ────────────

    [Fact]
    public void Default_LoginScriptTimeout_IsShort()
    {
        // Payloads that run in a signed-in session are bounded tightly on
        // purpose. They run sequentially, so whatever one waits for the whole
        // desktop waits for, and someone is standing at the machine. A login
        // payload that needs minutes has already failed at its job.
        StartSetPreferences.Default.LoginScriptTimeout.Should().Be(120);
    }

    [Fact]
    public void Default_LoginScriptTimeout_IsFarBelowScriptTimeout()
    {
        // The regression this guards: login payloads once inherited
        // ScriptTimeout's hour, which is not a timeout so much as the absence of
        // one. A payload blocked in a cross-process call to the shell stopped an
        // entire login batch, blocked shutdown so the machine could not be
        // restarted remotely, and stalled the software-management agent for
        // eighty minutes -- all presenting as a frozen desktop with nothing
        // logged as a failure. Anyone raising this value should have to argue for
        // it against that.
        var prefs = StartSetPreferences.Default;
        prefs.LoginScriptTimeout.Should().BeLessThan(prefs.ScriptTimeout);
        prefs.LoginScriptTimeout.Should().BeLessThanOrEqualTo(300,
            "a payload someone is waiting on must not be allowed to hold the session for minutes on end");
    }

    [Fact]
    public void Default_LoginBatchBudget_BoundsTheWholeBatch()
    {
        // The per-script bound multiplies: twelve payloads each burning
        // LoginScriptTimeout is twenty-four minutes of unfinished desktop, which
        // the person at the machine cannot tell apart from the hang it replaced.
        // Measured on a lab workstation 2026-09-09, where consecutive payloads
        // timed out one after another exactly as that arithmetic predicts.
        var prefs = StartSetPreferences.Default;
        prefs.LoginBatchBudget.Should().Be(300);
        prefs.LoginBatchBudget.Should().BeGreaterThan(prefs.LoginScriptTimeout,
            "a batch must be allowed to run at least one full-length payload");
        prefs.LoginBatchBudget.Should().BeLessThan(prefs.LoginScriptTimeout * 12,
            "the whole point is that the batch cannot cost the sum of every payload's timeout");
    }

    [Fact]
    public void Default_ShellReadyTimeout_WaitsForTheDesktop()
    {
        // Login payloads are triggered by event 4624, which fires when
        // authentication succeeds -- before userinit, before the shell exists.
        // Running them then is what made them hang, because a payload that asks
        // the shell for something before there is a shell simply waits. A zero
        // here would restore exactly that.
        StartSetPreferences.Default.ShellReadyTimeout.Should().BeGreaterThan(0);
        StartSetPreferences.Default.ShellReadyTimeout.Should().Be(180);
    }

    [Fact]
    public void Default_ShellSettleDelay_IsNonZero()
    {
        // Explorer's process exists slightly before its message pump is serving,
        // and a payload arriving in that gap hits the very problem the readiness
        // wait exists to avoid.
        StartSetPreferences.Default.ShellSettleDelay.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Default_ShellReadyTimeout_ExceedsTheSettleDelay()
    {
        var prefs = StartSetPreferences.Default;
        prefs.ShellReadyTimeout.Should().BeGreaterThan(prefs.ShellSettleDelay,
            "the wait has to leave room for the shell to appear and then settle");
    }

    [Fact]
    public void Default_LoginScriptTimeout_IsPositive()
    {
        // Zero or negative would mean every login payload is killed on the spot.
        StartSetPreferences.Default.LoginScriptTimeout.Should().BeGreaterThan(0);
    }
}
