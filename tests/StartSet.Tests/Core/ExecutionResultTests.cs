using FluentAssertions;
using StartSet.Core.Enums;
using StartSet.Core.Models;

namespace StartSet.Tests.Core;

public class ExecutionResultTests
{
    private static ScriptPayload CreateTestScript(string name = "test.ps1") => new()
    {
        FilePath = $@"C:\ProgramData\ManagedState\boot-every\{name}",
        PayloadType = PayloadType.BootEvery
    };

    // ──────────────── Factory: Success ────────────────

    [Fact]
    public void Success_SetsCorrectDefaults()
    {
        var script = CreateTestScript();
        var result = ExecutionResult.Success(script, exitCode: 0);

        result.Status.Should().Be(ExecutionStatus.Success);
        result.ExitCode.Should().Be(0);
        result.IsSuccess.Should().BeTrue();
        result.Script.Should().BeSameAs(script);
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Success_CapturesOutput()
    {
        var script = CreateTestScript();
        var result = ExecutionResult.Success(script, 0, stdout: "hello", stderr: "warn");

        result.StandardOutput.Should().Be("hello");
        result.StandardError.Should().Be("warn");
    }

    // ──────────────── Factory: Failed ────────────────

    [Fact]
    public void Failed_SetsCorrectStatus()
    {
        var script = CreateTestScript();
        var result = ExecutionResult.Failed(script, exitCode: 1, errorMessage: "boom");

        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ExitCode.Should().Be(1);
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("boom");
    }

    // ──────────────── Factory: Skipped ────────────────

    [Fact]
    public void Skipped_SetsReasonAsErrorMessage()
    {
        var script = CreateTestScript();
        var result = ExecutionResult.Skipped(script, "Already executed (run-once)");

        result.Status.Should().Be(ExecutionStatus.Skipped);
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("Already executed (run-once)");
        result.ExitCode.Should().BeNull();
    }

    // ──────────────── Factory: Timeout ────────────────

    [Fact]
    public void Timeout_IncludesElapsedTime()
    {
        var script = CreateTestScript();
        var result = ExecutionResult.Timeout(script, TimeSpan.FromSeconds(60));

        result.Status.Should().Be(ExecutionStatus.Timeout);
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("60");
        result.ErrorMessage.Should().Contain("timed out");
    }

    // ──────────────── Duration ────────────────

    [Fact]
    public void Duration_WhenEndTimeSet_ReturnsSpan()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddSeconds(5);

        var result = new ExecutionResult
        {
            Script = CreateTestScript(),
            Status = ExecutionStatus.Success,
            StartTime = start,
            EndTime = end
        };

        result.Duration.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Duration_WhenNoEndTime_ReturnsNull()
    {
        var result = new ExecutionResult
        {
            Script = CreateTestScript(),
            Status = ExecutionStatus.Success,
            StartTime = DateTimeOffset.UtcNow
        };

        result.Duration.Should().BeNull();
    }
}
