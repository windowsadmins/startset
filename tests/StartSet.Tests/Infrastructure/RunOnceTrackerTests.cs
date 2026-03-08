using FluentAssertions;
using StartSet.Core.Enums;
using StartSet.Core.Models;
using StartSet.Infrastructure.Tracking;
using StartSet.Tests.Helpers;

namespace StartSet.Tests.Infrastructure;

public class RunOnceTrackerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private RunOnceTracker CreateTracker(string filename = "runonce.json")
    {
        return new RunOnceTracker(Path.Combine(_temp.Path, filename), isCustomPath: true);
    }

    private static ScriptPayload CreateScript(string name = "test.ps1") => new()
    {
        FilePath = $@"C:\ProgramData\ManagedScripts\boot-once\{name}",
        PayloadType = PayloadType.BootOnce,
        Checksum = "abc123"
    };

    // ──────────────── HasExecuted ────────────────

    [Fact]
    public void HasExecuted_NewTracker_ReturnsFalse()
    {
        var tracker = CreateTracker();
        tracker.HasExecuted(@"C:\scripts\test.ps1").Should().BeFalse();
    }

    [Fact]
    public void HasExecuted_AfterRecord_ReturnsTrue()
    {
        var tracker = CreateTracker();
        var script = CreateScript();

        tracker.RecordExecution(ExecutionResult.Success(script, 0));

        tracker.HasExecuted(script.FilePath).Should().BeTrue();
    }

    [Fact]
    public void HasExecuted_WithMatchingChecksum_ReturnsTrue()
    {
        var tracker = CreateTracker();
        var script = CreateScript();
        script.Checksum = "sha256hash";

        tracker.RecordExecution(ExecutionResult.Success(script, 0));

        tracker.HasExecuted(script.FilePath, "sha256hash").Should().BeTrue();
    }

    [Fact]
    public void HasExecuted_WithChangedChecksum_ReturnsFalse()
    {
        var tracker = CreateTracker();
        var script = CreateScript();
        script.Checksum = "original_hash";

        tracker.RecordExecution(ExecutionResult.Success(script, 0));

        // Script content changed — should re-run
        tracker.HasExecuted(script.FilePath, "different_hash").Should().BeFalse();
    }

    [Fact]
    public void HasExecuted_FailedExecution_ReturnsFalse()
    {
        var tracker = CreateTracker();
        var script = CreateScript();

        tracker.RecordExecution(ExecutionResult.Failed(script, 1, "error"));

        // Failed executions should not count as "executed"
        tracker.HasExecuted(script.FilePath).Should().BeFalse();
    }

    // ──────────────── RecordExecution ────────────────

    [Fact]
    public void RecordExecution_PersistsToDisk()
    {
        var filePath = Path.Combine(_temp.Path, "persist.json");
        var tracker1 = new RunOnceTracker(filePath, isCustomPath: true);
        var script = CreateScript();

        tracker1.RecordExecution(ExecutionResult.Success(script, 0));

        // Create a new tracker reading the same file
        var tracker2 = new RunOnceTracker(filePath, isCustomPath: true);
        tracker2.HasExecuted(script.FilePath).Should().BeTrue();
    }

    [Fact]
    public void RecordExecution_OverwritesPreviousEntry()
    {
        var tracker = CreateTracker();
        var script = CreateScript();
        script.Checksum = "v1";

        tracker.RecordExecution(ExecutionResult.Success(script, 0));

        script.Checksum = "v2";
        tracker.RecordExecution(ExecutionResult.Success(script, 0));

        // Should match the latest checksum
        tracker.HasExecuted(script.FilePath, "v2").Should().BeTrue();
        tracker.HasExecuted(script.FilePath, "v1").Should().BeFalse();
    }

    // ──────────────── ClearExecution ────────────────

    [Fact]
    public void ClearExecution_RemovesEntry()
    {
        var tracker = CreateTracker();
        var script = CreateScript();

        tracker.RecordExecution(ExecutionResult.Success(script, 0));
        tracker.ClearExecution(script.FilePath).Should().BeTrue();
        tracker.HasExecuted(script.FilePath).Should().BeFalse();
    }

    [Fact]
    public void ClearExecution_NonExistent_ReturnsFalse()
    {
        var tracker = CreateTracker();
        tracker.ClearExecution(@"C:\nonexistent.ps1").Should().BeFalse();
    }

    // ──────────────── ClearAll ────────────────

    [Fact]
    public void ClearAll_RemovesEverything()
    {
        var tracker = CreateTracker();

        tracker.RecordExecution(ExecutionResult.Success(CreateScript("a.ps1"), 0));
        tracker.RecordExecution(ExecutionResult.Success(CreateScript("b.ps1"), 0));
        tracker.RecordExecution(ExecutionResult.Success(CreateScript("c.ps1"), 0));

        tracker.ClearAll();

        tracker.Count.Should().Be(0);
        tracker.GetAllExecutions().Should().BeEmpty();
    }

    // ──────────────── Count and GetAll ────────────────

    [Fact]
    public void Count_ReflectsTrackedEntries()
    {
        var tracker = CreateTracker();

        tracker.Count.Should().Be(0);

        tracker.RecordExecution(ExecutionResult.Success(CreateScript("a.ps1"), 0));
        tracker.Count.Should().Be(1);

        tracker.RecordExecution(ExecutionResult.Success(CreateScript("b.ps1"), 0));
        tracker.Count.Should().Be(2);
    }

    [Fact]
    public void GetAllExecutions_ReturnsReadOnlySnapshot()
    {
        var tracker = CreateTracker();
        tracker.RecordExecution(ExecutionResult.Success(CreateScript("test.ps1"), 0));

        var all = tracker.GetAllExecutions();
        all.Should().HaveCount(1);
    }

    // ──────────────── MarkAsExecuted ────────────────

    [Fact]
    public void MarkAsExecuted_ManualEntry()
    {
        var tracker = CreateTracker();
        tracker.MarkAsExecuted(@"C:\scripts\manual.ps1", "checksum123", "BootOnce");

        tracker.HasExecuted(@"C:\scripts\manual.ps1").Should().BeTrue();
        tracker.HasExecuted(@"C:\scripts\manual.ps1", "checksum123").Should().BeTrue();
    }

    // ──────────────── Reload ────────────────

    [Fact]
    public void Reload_RefreshesFromDisk()
    {
        var filePath = Path.Combine(_temp.Path, "reload.json");
        var tracker1 = new RunOnceTracker(filePath, isCustomPath: true);
        var tracker2 = new RunOnceTracker(filePath, isCustomPath: true);

        // Write via tracker1
        tracker1.RecordExecution(ExecutionResult.Success(CreateScript(), 0));

        // tracker2 doesn't see it yet (loaded at construction)
        tracker2.HasExecuted(CreateScript().FilePath).Should().BeFalse();

        // After reload, should see it
        tracker2.Reload();
        tracker2.HasExecuted(CreateScript().FilePath).Should().BeTrue();
    }
}
