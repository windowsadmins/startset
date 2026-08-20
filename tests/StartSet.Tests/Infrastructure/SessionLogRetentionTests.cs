using System;
using System.IO;
using StartSet.Infrastructure.Logging;
using Xunit;

namespace StartSet.Tests.Infrastructure;

/// <summary>
/// Retention over loose files in the log directory.
/// </summary>
/// <remarks>
/// The regression these guard: the sweep globbed "startset*.log", which covered the
/// legacy Serilog rotation and nothing else. Payload scripts write their own files into
/// the same directory under their own names, and none of them were ever expired.
/// </remarks>
public class SessionLogRetentionTests : IDisposable
{
    private readonly string _root;

    public SessionLogRetentionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "startset-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteFile(string relativePath, DateTime lastWrite)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
        File.SetLastWriteTime(full, lastWrite);
        return full;
    }

    private static DateTime Cutoff => DateTime.Now.AddDays(-30);
    private static DateTime Expired => DateTime.Now.AddDays(-45);
    private static DateTime Fresh => DateTime.Now.AddDays(-2);

    [Fact]
    public void ExpiresLogsWrittenByPayloadScripts()
    {
        // These do not match the old "startset*.log" glob, which is why they survived
        // indefinitely.
        var old = WriteFile("SystemKeepTime_20250101_120000.log", Expired);
        var recent = WriteFile("SystemKeepTime_20260801_120000.log", Fresh);

        SessionLogger.SweepExpiredFiles(_root, Cutoff);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void StillExpiresTheLegacyRotationFiles()
    {
        var legacy = WriteFile("startset20250101.log", Expired);

        SessionLogger.SweepExpiredFiles(_root, Cutoff);

        Assert.False(File.Exists(legacy));
    }

    [Fact]
    public void DoesNotDescendIntoSessionDirectories()
    {
        // A session directory is aged as a unit by its own date-named rule. Picking old
        // files out of one individually would leave half a session's log set behind.
        var nested = WriteFile(Path.Combine("2026-01-02", "0800", "startset.log"), Expired);

        SessionLogger.SweepExpiredFiles(_root, Cutoff);

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void IsSilentWhenTheDirectoryDoesNotExist()
    {
        var missing = Path.Combine(_root, "no-such-directory");

        SessionLogger.SweepExpiredFiles(missing, Cutoff);

        Assert.False(Directory.Exists(missing));
    }
}
