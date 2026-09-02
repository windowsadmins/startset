using System;
using System.IO;
using StartSet.Core.Enums;
using StartSet.Core.Models;
using StartSet.Engine.Processors;
using StartSet.Infrastructure.Logging;
using Xunit;

namespace StartSet.Tests.Engine;

/// <summary>
/// A user-context payload that cannot reach the signed-in user's session must be
/// recorded as not having run.
/// </summary>
/// <remarks>
/// The behaviour these cover replaced a fallback that ran the payload as SYSTEM
/// instead. That fallback could not apply a per-user setting from session 0, and
/// because the status came from the SYSTEM process's exit code -- which was
/// usually 0, since the scripts guard on "skip administrators" and SYSTEM is one
/// -- a lost race and a clean run were indistinguishable downstream.
/// </remarks>
public class UserContextDeferralTests
{
    private static ScriptPayload Payload(PayloadType type, string fileName = "Thing.ps1") => new()
    {
        FilePath = Path.Combine("payloads", fileName),
        PayloadType = type
    };

    [Theory]
    [InlineData(PayloadType.LoginOnce)]
    [InlineData(PayloadType.LoginEvery)]
    [InlineData(PayloadType.OnDemand)]
    public void RequiresUserContext_IsTrueForUserPayloadTypes(PayloadType type)
    {
        Assert.True(UserContextExecution.RequiresUserContext(type));
    }

    [Theory]
    [InlineData(PayloadType.BootOnce)]
    [InlineData(PayloadType.BootEvery)]
    [InlineData(PayloadType.LoginPrivilegedOnce)]
    [InlineData(PayloadType.LoginPrivilegedEvery)]
    [InlineData(PayloadType.OnDemandPrivileged)]
    public void RequiresUserContext_IsFalseForSystemPayloadTypes(PayloadType type)
    {
        Assert.False(UserContextExecution.RequiresUserContext(type));
    }

    [Fact]
    public void Deferred_IsNeitherSuccessNorFailure()
    {
        var result = ExecutionResult.Deferred(Payload(PayloadType.LoginEvery), "no console session");

        Assert.Equal(ExecutionStatus.Deferred, result.Status);
        Assert.False(result.IsSuccess);
        Assert.NotEqual(ExecutionStatus.Failed, result.Status);
        // Nothing ran, so there is no exit code to report. A zero here would read
        // as a clean run, which is the confusion this status exists to end.
        Assert.Null(result.ExitCode);
    }

    [Fact]
    public void Deferred_KeepsTheReasonItCouldNotRun()
    {
        var result = ExecutionResult.Deferred(
            Payload(PayloadType.LoginEvery),
            "Not run: could not start in the console user's session (WTSQueryUserToken failed)");

        Assert.Contains("could not start in the console user's session", result.ErrorMessage);
    }

    [Fact]
    public void ItemRecord_ReportsDeferredAsPendingRatherThanInstalled()
    {
        var script = Payload(PayloadType.LoginEvery);
        var result = ExecutionResult.Deferred(script, "no console session");

        var record = SessionLogger.BuildItemRecord(script, result, "session-1", DateTime.UtcNow);

        // Pending, not Installed: the settings were not applied.
        Assert.Equal("Pending", record.CurrentStatus);
        // ...and not Error either: the next sign-in retries, and a fleet-wide
        // alarm for a self-healing condition is its own kind of noise.
        Assert.NotEqual("Error", record.CurrentStatus);
        Assert.Equal(0, record.FailureCount);
    }

    [Fact]
    public void ItemRecord_DeferredRecordsNoActionPerformed()
    {
        var script = Payload(PayloadType.LoginEvery);
        var result = ExecutionResult.Deferred(script, "no console session");

        var record = SessionLogger.BuildItemRecord(script, result, "session-1", DateTime.UtcNow);

        Assert.True(string.IsNullOrEmpty(record.LastSeenInSession));
    }

    [Fact]
    public void ItemRecord_SuccessStillReportsInstalled()
    {
        var script = Payload(PayloadType.LoginEvery);
        var result = ExecutionResult.Success(script, exitCode: 0);

        var record = SessionLogger.BuildItemRecord(script, result, "session-1", DateTime.UtcNow);

        Assert.Equal("Installed", record.CurrentStatus);
    }
}
