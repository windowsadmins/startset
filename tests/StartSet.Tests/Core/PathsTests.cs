using FluentAssertions;
using StartSet.Core.Constants;

namespace StartSet.Tests.Core;

public class PathsTests
{
    [Fact]
    public void ScriptRoot_IsUnderProgramData()
    {
        Paths.ScriptRoot.Should().StartWith(@"C:\ProgramData\");
    }

    [Fact]
    public void AllPayloadDirectories_ContainsExpectedCount()
    {
        // 9 payload dirs + share + logs = 11
        Paths.AllPayloadDirectories.Should().HaveCount(11);
    }

    [Fact]
    public void AllPayloadDirectories_AreUnderScriptRoot()
    {
        foreach (var dir in Paths.AllPayloadDirectories)
        {
            dir.Should().StartWith(Paths.ScriptRoot,
                $"directory '{dir}' should be under ScriptRoot");
        }
    }

    [Fact]
    public void AllPayloadDirectories_AreUnique()
    {
        Paths.AllPayloadDirectories.Should().OnlyHaveUniqueItems();
    }

    // ──────────────── GetRunOnceFilePath ────────────────

    [Fact]
    public void GetRunOnceFilePath_System_ReturnsSystemFile()
    {
        var path = Paths.GetRunOnceFilePath();
        path.Should().Contain("runonce-system.json");
        path.Should().StartWith(Paths.ShareDir);
    }

    [Fact]
    public void GetRunOnceFilePath_NullUsername_ReturnsSystemFile()
    {
        var path = Paths.GetRunOnceFilePath(null);
        path.Should().Contain("runonce-system.json");
    }

    [Fact]
    public void GetRunOnceFilePath_EmptyUsername_ReturnsSystemFile()
    {
        var path = Paths.GetRunOnceFilePath("");
        path.Should().Contain("runonce-system.json");
    }

    [Fact]
    public void GetRunOnceFilePath_WithUsername_ReturnsUserFile()
    {
        var path = Paths.GetRunOnceFilePath("jsmith");
        path.Should().Contain("runonce-jsmith.json");
        path.Should().StartWith(Paths.ShareDir);
    }

    // ──────────────── Trigger files ────────────────

    [Fact]
    public void TriggerFiles_AreHiddenFiles()
    {
        // StartSet trigger files start with . (hidden convention)
        Paths.TriggerOnDemand.Should().Contain(".startset.");
        Paths.TriggerOnDemandPrivileged.Should().Contain(".startset.");
        Paths.TriggerLoginPrivileged.Should().Contain(".startset.");
        Paths.TriggerCleanup.Should().Contain(".startset.");
    }

    // ──────────────── Install paths ────────────────

    [Fact]
    public void InstallDirectory_IsUnderProgramFiles()
    {
        Paths.InstallDirectory.Should().StartWith(@"C:\Program Files\");
    }

    [Fact]
    public void LogFilePath_IsUnderLogDirectory()
    {
        Paths.LogFilePath.Should().StartWith(Paths.LogDirectory);
    }
}
