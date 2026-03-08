using FluentAssertions;
using StartSet.Core.Enums;
using StartSet.Core.Models;
using StartSet.Infrastructure.Tracking;
using StartSet.Tests.Helpers;

namespace StartSet.Tests.Integration;

/// <summary>
/// Tests the run-once tracking lifecycle:
/// execute → record → re-execute → skip → modify → detect change → re-execute.
/// </summary>
public class RunOnceLifecycleTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private ScriptPayload MakeScript(string name, string content = "Write-Output 'hello'")
    {
        var path = _temp.CreateFile(name, content);
        return new ScriptPayload
        {
            FilePath = path,
            PayloadType = PayloadType.BootOnce,
            Checksum = $"sha256:{Guid.NewGuid():N}" // simulate unique checksum
        };
    }

    [Fact]
    public void NewScript_HasNotBeenExecuted()
    {
        var trackerPath = _temp.CreateFile("runonce.json", "{}");
        var tracker = new RunOnceTracker(trackerPath, isCustomPath: true);
        var script = MakeScript("new.ps1");

        tracker.HasExecuted(script.FilePath, script.Checksum).Should().BeFalse();
    }

    [Fact]
    public void RecordExecution_MarksThatScriptAsExecuted()
    {
        var trackerPath = _temp.CreateFile("runonce.json", "{}");
        var tracker = new RunOnceTracker(trackerPath, isCustomPath: true);
        var script = MakeScript("recorded.ps1");

        var result = ExecutionResult.Success(script, exitCode: 0, stdout: "ok");
        tracker.RecordExecution(result);

        tracker.HasExecuted(script.FilePath, script.Checksum).Should().BeTrue();
    }

    [Fact]
    public void ChecksumChange_DetectedAsNotExecuted()
    {
        var trackerPath = _temp.CreateFile("runonce.json", "{}");
        var tracker = new RunOnceTracker(trackerPath, isCustomPath: true);
        var script = MakeScript("change.ps1");

        var result = ExecutionResult.Success(script, exitCode: 0);
        tracker.RecordExecution(result);

        // Same path, different checksum → should NOT be considered executed
        tracker.HasExecuted(script.FilePath, "sha256:different_checksum").Should().BeFalse();
    }

    [Fact]
    public void ClearExecution_AllowsReExecution()
    {
        var trackerPath = _temp.CreateFile("runonce.json", "{}");
        var tracker = new RunOnceTracker(trackerPath, isCustomPath: true);
        var script = MakeScript("cleared.ps1");

        var result = ExecutionResult.Success(script, exitCode: 0);
        tracker.RecordExecution(result);
        tracker.HasExecuted(script.FilePath, script.Checksum).Should().BeTrue();

        tracker.ClearExecution(script.FilePath);
        tracker.HasExecuted(script.FilePath, script.Checksum).Should().BeFalse();
    }

    [Fact]
    public void ClearAll_ClearsEveryTrackedScript()
    {
        var trackerPath = _temp.CreateFile("runonce.json", "{}");
        var tracker = new RunOnceTracker(trackerPath, isCustomPath: true);
        var script1 = MakeScript("one.ps1");
        var script2 = MakeScript("two.ps1");

        tracker.RecordExecution(ExecutionResult.Success(script1, 0));
        tracker.RecordExecution(ExecutionResult.Success(script2, 0));

        tracker.ClearAll();

        tracker.HasExecuted(script1.FilePath, script1.Checksum).Should().BeFalse();
        tracker.HasExecuted(script2.FilePath, script2.Checksum).Should().BeFalse();
    }

    [Fact]
    public void Persistence_SurvivesReload()
    {
        var trackerPath = _temp.CreateFile("runonce.json", "{}");
        var tracker1 = new RunOnceTracker(trackerPath, isCustomPath: true);
        var script = MakeScript("persist.ps1");

        tracker1.RecordExecution(ExecutionResult.Success(script, 0));

        // Create a new tracker instance reading the same file
        var tracker2 = new RunOnceTracker(trackerPath, isCustomPath: true);
        tracker2.HasExecuted(script.FilePath, script.Checksum).Should().BeTrue();
    }

    [Fact]
    public void FailedExecution_NotTracked()
    {
        var trackerPath = _temp.CreateFile("runonce.json", "{}");
        var tracker = new RunOnceTracker(trackerPath, isCustomPath: true);
        var script = MakeScript("failed.ps1");

        var result = ExecutionResult.Failed(script, exitCode: 1, errorMessage: "script error");
        tracker.RecordExecution(result);

        // Failed executions should not be tracked as "executed"
        tracker.HasExecuted(script.FilePath, script.Checksum).Should().BeFalse();
    }

    [Fact]
    public void MultipleScripts_TrackedIndependently()
    {
        var trackerPath = _temp.CreateFile("runonce.json", "{}");
        var tracker = new RunOnceTracker(trackerPath, isCustomPath: true);
        var script1 = MakeScript("alpha.ps1");
        var script2 = MakeScript("beta.ps1");

        tracker.RecordExecution(ExecutionResult.Success(script1, 0));

        tracker.HasExecuted(script1.FilePath, script1.Checksum).Should().BeTrue();
        tracker.HasExecuted(script2.FilePath, script2.Checksum).Should().BeFalse();
    }
}
