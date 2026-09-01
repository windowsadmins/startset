using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using StartSet.Core.Enums;
using StartSet.Core.Models;
using StartSet.Infrastructure.Logging;
using Xunit;

namespace StartSet.Tests.Infrastructure;

/// <summary>
/// Shape and content of reports/items.json.
/// </summary>
/// <remarks>
/// The record has to keep the field names Cimian writes for its managed items, so one
/// reader can consume both tools' reports and tell them apart by <c>type</c>.
/// </remarks>
public class ItemsReportTests : IDisposable
{
    private readonly string _root;

    public ItemsReportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "startset-items-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly string[] SharedFieldNames =
    [
        "id", "item_name", "display_name", "item_type", "current_status", "latest_version",
        "last_seen_in_session", "last_attempt_time", "last_attempt_status", "last_update",
        "failure_count", "warning_count", "type", "last_error"
    ];

    private static ScriptPayload Payload(string fileName, PayloadType type = PayloadType.BootEvery) => new()
    {
        FilePath = Path.Combine("payloads", fileName),
        PayloadType = type
    };

    private static readonly DateTime Now = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WritesOneRecordPerPayloadWithTheSharedFieldNames()
    {
        var ok = SessionLogger.BuildItemRecord(
            Payload("Set Timezone.ps1"),
            ExecutionResult.Success(Payload("Set Timezone.ps1"), 0),
            "2026-09-01-1200", Now);
        var failed = SessionLogger.BuildItemRecord(
            Payload("Tools.msi", PayloadType.BootOnce),
            ExecutionResult.Failed(Payload("Tools.msi", PayloadType.BootOnce), 1603, "Fatal error during installation"),
            "2026-09-01-1200", Now);

        var reportsDir = Path.Combine(_root, "reports");
        SessionLogger.WriteItemsReport(reportsDir, [ok, failed]);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(reportsDir, "items.json")));
        var records = doc.RootElement.EnumerateArray().ToList();

        Assert.Equal(2, records.Count);
        foreach (var record in records)
        {
            foreach (var field in SharedFieldNames)
                Assert.True(record.TryGetProperty(field, out _), $"missing field {field}");
            Assert.Equal("startset", record.GetProperty("type").GetString());
        }

        var script = records[0];
        Assert.Equal("boot-every/settimezone.ps1", script.GetProperty("id").GetString());
        Assert.Equal("Set Timezone.ps1", script.GetProperty("item_name").GetString());
        Assert.Equal("Set Timezone", script.GetProperty("display_name").GetString());
        Assert.Equal("ps1", script.GetProperty("item_type").GetString());
        Assert.Equal("Installed", script.GetProperty("current_status").GetString());
        Assert.Equal("2026-09-01-1200", script.GetProperty("last_seen_in_session").GetString());
        Assert.Equal("execute", script.GetProperty("action_performed").GetString());
        Assert.Equal("", script.GetProperty("last_error").GetString());
        Assert.Equal(0, script.GetProperty("failure_count").GetInt32());

        var package = records[1];
        Assert.Equal("boot-once/tools.msi", package.GetProperty("id").GetString());
        Assert.Equal("msi", package.GetProperty("item_type").GetString());
        Assert.Equal("Error", package.GetProperty("current_status").GetString());
        Assert.Equal("Error", package.GetProperty("last_attempt_status").GetString());
        Assert.Equal("install", package.GetProperty("action_performed").GetString());
        Assert.Equal("Fatal error during installation", package.GetProperty("last_error").GetString());
        Assert.Equal(1, package.GetProperty("failure_count").GetInt32());
    }

    [Fact]
    public void SkippedPayloadIsNotStampedWithTheSession()
    {
        // Same rule as Cimian: the session id marks what the run acted on, so a payload
        // it only looked at carries no session and no action.
        var payload = Payload("Once.ps1", PayloadType.LoginOnce);
        payload.AlreadyExecuted = true;
        var record = SessionLogger.BuildItemRecord(
            payload, ExecutionResult.Skipped(payload, "Already executed (run-once)"), "2026-09-01-1200", Now);

        Assert.Equal("", record.LastSeenInSession);
        Assert.Null(record.ActionPerformed);
        Assert.Equal("Installed", record.CurrentStatus);
    }

    [Fact]
    public void SkippedForAnyOtherReasonIsPending()
    {
        var payload = Payload("Later.ps1");
        var record = SessionLogger.BuildItemRecord(
            payload, ExecutionResult.Skipped(payload, "Network unavailable"), "2026-09-01-1200", Now);

        Assert.Equal("Pending", record.CurrentStatus);
    }

    [Fact]
    public void FailureWithoutAMessageFallsBackToStderrThenExitCode()
    {
        var payload = Payload("Noisy.cmd");
        var withStderr = ExecutionResult.Failed(payload, 2);
        withStderr.StandardError = "\r\nAccess is denied.\r\n";
        Assert.Equal("Access is denied.",
            SessionLogger.BuildItemRecord(payload, withStderr, "s", Now).LastError);

        var silent = ExecutionResult.Failed(payload, 2);
        Assert.Equal("Exit code 2",
            SessionLogger.BuildItemRecord(payload, silent, "s", Now).LastError);
    }

    [Fact]
    public void StderrOnASuccessfulRunIsRecordedAsAWarning()
    {
        var payload = Payload("Chatty.ps1");
        var result = ExecutionResult.Success(payload, 0, stderr: "WARNING: deprecated cmdlet\n");

        var record = SessionLogger.BuildItemRecord(payload, result, "s", Now);

        Assert.Equal(1, record.WarningCount);
        Assert.Equal("WARNING: deprecated cmdlet", record.LastWarning);
        Assert.Equal("Installed", record.CurrentStatus);
    }

    [Fact]
    public void CountsFailedExecutionsFromRecentSessionEventStreams()
    {
        var recent = Path.Combine(_root, "2026-09-01", "1100");
        var old = Path.Combine(_root, "2026-08-01", "1100");
        Directory.CreateDirectory(recent);
        Directory.CreateDirectory(old);

        static string Event(string name, string status, DateTime at) =>
            $"{{\"event_type\":\"script_execution\",\"script_name\":\"{name}\",\"status\":\"{status}\",\"timestamp\":\"{at:o}\"}}";

        File.WriteAllLines(Path.Combine(recent, "events.jsonl"),
        [
            Event("Flaky.ps1", "failed", Now.AddHours(-1)),
            Event("Flaky.ps1", "failed", Now.AddHours(-2)),
            Event("Fine.ps1", "completed", Now.AddHours(-1)),
            "not json at all",
            ""
        ]);
        File.WriteAllLines(Path.Combine(old, "events.jsonl"),
        [
            Event("Flaky.ps1", "failed", Now.AddDays(-31))
        ]);

        var counts = SessionLogger.CountRecentFailures([recent, old, Path.Combine(_root, "missing")], Now.AddDays(-7));

        Assert.Equal(2, counts["Flaky.ps1"]);
        Assert.False(counts.ContainsKey("Fine.ps1"));
    }
}
