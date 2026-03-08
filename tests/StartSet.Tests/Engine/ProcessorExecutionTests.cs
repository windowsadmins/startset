using FluentAssertions;
using StartSet.Core.Enums;
using StartSet.Core.Models;
using StartSet.Engine.Processors;
using StartSet.Tests.Helpers;

namespace StartSet.Tests.Engine;

public class ProcessorExecutionTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    public void Dispose() => _temp.Dispose();

    // ──────────────── PowerShell execution ────────────────

    [Fact]
    public async Task PowerShell_SuccessfulScript_ReturnsSuccess()
    {
        var scriptPath = _temp.CreateFile("success.ps1", "Write-Output 'Hello from StartSet'");
        var script = new ScriptPayload
        {
            FilePath = scriptPath,
            PayloadType = PayloadType.BootEvery
        };

        var processor = new PowerShellProcessor();
        var result = await processor.ExecuteAsync(script, _timeout);

        result.Status.Should().Be(ExecutionStatus.Success);
        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Contain("Hello from StartSet");
    }

    [Fact]
    public async Task PowerShell_ExitCode1_ReturnsFailed()
    {
        var scriptPath = _temp.CreateFile("fail.ps1", "exit 1");
        var script = new ScriptPayload
        {
            FilePath = scriptPath,
            PayloadType = PayloadType.BootEvery
        };

        var processor = new PowerShellProcessor();
        var result = await processor.ExecuteAsync(script, _timeout);

        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ExitCode.Should().Be(1);
    }

    [Fact]
    public async Task PowerShell_WritesStderr()
    {
        var scriptPath = _temp.CreateFile("stderr.ps1", "Write-Error 'something went wrong' 2>&1; exit 0");
        var script = new ScriptPayload
        {
            FilePath = scriptPath,
            PayloadType = PayloadType.BootEvery
        };

        var processor = new PowerShellProcessor();
        var result = await processor.ExecuteAsync(script, _timeout);

        // stderr should be captured
        (result.StandardOutput + result.StandardError).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PowerShell_Timeout_ReturnsTimeout()
    {
        var scriptPath = _temp.CreateFile("hang.ps1", "Start-Sleep -Seconds 120");
        var script = new ScriptPayload
        {
            FilePath = scriptPath,
            PayloadType = PayloadType.BootEvery
        };

        var processor = new PowerShellProcessor();
        var result = await processor.ExecuteAsync(script, TimeSpan.FromSeconds(2));

        result.Status.Should().Be(ExecutionStatus.Timeout);
    }

    [Fact]
    public async Task PowerShell_SetsDuration()
    {
        var scriptPath = _temp.CreateFile("timed.ps1", "Write-Output 'done'");
        var script = new ScriptPayload
        {
            FilePath = scriptPath,
            PayloadType = PayloadType.BootEvery
        };

        var processor = new PowerShellProcessor();
        var result = await processor.ExecuteAsync(script, _timeout);

        result.StartTime.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
        result.EndTime.Should().NotBeNull();
        result.Duration.Should().NotBeNull();
        result.Duration!.Value.Should().BePositive();
    }

    // ──────────────── Batch execution ────────────────

    [Fact]
    public async Task Batch_SuccessfulScript_ReturnsSuccess()
    {
        var scriptPath = _temp.CreateFile("success.cmd", "@echo Hello from CMD");
        var script = new ScriptPayload
        {
            FilePath = scriptPath,
            PayloadType = PayloadType.BootEvery
        };

        var processor = new BatchProcessor();
        var result = await processor.ExecuteAsync(script, _timeout);

        result.Status.Should().Be(ExecutionStatus.Success);
        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Contain("Hello from CMD");
    }

    [Fact]
    public async Task Batch_NonZeroExit_ReturnsFailed()
    {
        var scriptPath = _temp.CreateFile("fail.cmd", "@exit /b 42");
        var script = new ScriptPayload
        {
            FilePath = scriptPath,
            PayloadType = PayloadType.BootEvery
        };

        var processor = new BatchProcessor();
        var result = await processor.ExecuteAsync(script, _timeout);

        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ExitCode.Should().Be(42);
    }

    // ──────────────── Cancellation ────────────────

    [Fact]
    public async Task PowerShell_Cancellation_StopsExecution()
    {
        var scriptPath = _temp.CreateFile("cancel.ps1", "Start-Sleep -Seconds 120");
        var script = new ScriptPayload
        {
            FilePath = scriptPath,
            PayloadType = PayloadType.BootEvery
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var processor = new PowerShellProcessor();
        var result = await processor.ExecuteAsync(script, TimeSpan.FromSeconds(60), cts.Token);

        // Should be either Timeout or Cancelled — not Success
        result.Status.Should().NotBe(ExecutionStatus.Success);
    }
}
