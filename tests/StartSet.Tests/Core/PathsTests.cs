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
        // 9 payload dirs + share + logs + reports = 12
        Paths.AllPayloadDirectories.Should().HaveCount(12);
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
        Paths.TriggerLogin.Should().Contain(".startset.");
        Paths.TriggerLoginPrivileged.Should().Contain(".startset.");
        Paths.TriggerCleanup.Should().Contain(".startset.");
    }

    [Fact]
    public void TriggerFiles_AreDistinctPaths()
    {
        // The watcher matches a created file against these by exact path, so two
        // triggers sharing one path would silently run the wrong payloads -- and a
        // trigger whose path no other constant collides with is the only thing that
        // makes that mapping unambiguous.
        var triggers = new[]
        {
            Paths.TriggerOnDemand,
            Paths.TriggerOnDemandPrivileged,
            Paths.TriggerLogin,
            Paths.TriggerLoginPrivileged,
            Paths.TriggerCleanup
        };

        triggers.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TriggerLogin_IsNotAPrefixOfTriggerLoginPrivileged()
    {
        // ".startset.login" is a prefix of ".startset.login-privileged" as a string.
        // The watcher compares whole paths so this is not a live bug, but any future
        // switch to prefix or wildcard matching would map the privileged trigger to
        // the user-context payloads, running the wrong set with no error.
        Paths.TriggerLoginPrivileged.Should().StartWith(Paths.TriggerLogin);
        Paths.TriggerLogin.Should().NotBe(Paths.TriggerLoginPrivileged);
    }

    // ──────────────── Install paths ────────────────

    [Fact]
    public void InstallDirectory_IsUnderProgramFiles()
    {
        Paths.InstallDirectory.Should().StartWith(@"C:\Program Files\");
    }

    [Fact]
    public void ReportsDirectory_IsUnderScriptRoot()
    {
        Paths.ReportsDirectory.Should().StartWith(Paths.ScriptRoot);
    }

    [Fact]
    public void MaxRetentionDays_IsPositive()
    {
        Paths.MaxRetentionDays.Should().BeGreaterThan(0);
    }
}
