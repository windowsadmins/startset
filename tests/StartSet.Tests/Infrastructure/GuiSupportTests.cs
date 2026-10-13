using System.ComponentModel;
using FluentAssertions;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Gui;
using StartSet.Infrastructure.Logging;
using StartSet.Tests.Helpers;

namespace StartSet.Tests.Infrastructure;

/// <summary>The non-UI decisions behind Managed State Keeper.exe.</summary>
public class GuiSupportTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ── Prefs ────────────────────────────────────────────────────

    [Fact]
    public void EverySetting_HasAPrefsTabDescription()
    {
        PreferenceResolver.Settings.Select(s => s.Name)
            .Where(name => !SettingDescriptions.Has(name))
            .Should().BeEmpty("every setting must appear on the Prefs tab");
    }

    [Fact]
    public void EveryDescription_IsInAKnownGroup()
    {
        PreferenceResolver.Settings
            .Select(s => SettingDescriptions.For(s.Name).Group)
            .Should().OnlyContain(group => SettingDescriptions.Groups.Contains(group));
    }

    [Fact]
    public void A_ManagedSetting_IsNotEditable() => PrefsElevation.CanEdit(isPolicyManaged: true).Should().BeFalse();

    [Fact]
    public void An_UnmanagedSetting_IsEditable_WithoutElevation()
    {
        // Editability no longer depends on elevation: UAC is asked for at Save.
        PrefsElevation.CanEdit(isPolicyManaged: false).Should().BeTrue();
    }

    [Fact]
    public void Save_StartsTheAppElevated_ToApplyThePayload()
    {
        var start = PrefsElevation.BuildElevatedApply(@"C:\Program Files\StartSet\Managed State Keeper.exe", "startset-settings-abc");

        start.Verb.Should().Be("runas");
        start.UseShellExecute.Should().BeTrue();
        start.Arguments.Should().Be("--apply-settings startset-settings-abc");
        SettingsPayload.IsApplyRun(start.Arguments.Split(' ')).Should().BeTrue();
        start.WorkingDirectory.Should().Be(@"C:\Program Files\StartSet");
    }

    [Fact]
    public void CancelledUac_IsNotAnError()
    {
        PrefsElevation.IsElevationCancelled(new Win32Exception(PrefsElevation.ErrorCancelled)).Should().BeTrue();
        PrefsElevation.IsElevationCancelled(new Win32Exception(5)).Should().BeFalse();
    }

    // ── Run ──────────────────────────────────────────────────────

    [Fact]
    public void OnDemand_GoesThroughTheTriggerFolder()
    {
        RunModes.UsesTrigger(RunMode.OnDemand).Should().BeTrue();
        RunModes.TriggerPath(RunMode.OnDemand).Should().Be(@"C:\ProgramData\ManagedState\triggers\.startset.ondemand");
        RunModes.SessionRunType(RunMode.OnDemand).Should().Be("trigger");
    }

    [Theory]
    [InlineData(RunMode.OnDemandPrivileged, "on-demand --privileged --verbose", "on-demand")]
    [InlineData(RunMode.LoginPrivileged, "login-privileged --verbose", "login-privileged")]
    public void PrivilegedRuns_StartTheCli(RunMode mode, string arguments, string runType)
    {
        RunModes.UsesTrigger(mode).Should().BeFalse();
        string.Join(" ", RunModes.CliArguments(mode)).Should().Be(arguments);
        // The CLI names its session after its first argument.
        RunModes.SessionRunType(mode).Should().Be(RunModes.CliArguments(mode)[0]);
        RunModes.SessionRunType(mode).Should().Be(runType);
    }

    // ── Logs ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("1402-on-demand", 14, 2, "on-demand")]
    [InlineData("0905-login-privileged_2", 9, 5, "login-privileged")]
    [InlineData("2359-trigger", 23, 59, "trigger")]
    [InlineData("0800", 8, 0, "session")]
    public void SessionFolder_Parses(string folder, int hour, int minute, string runType)
    {
        var parsed = SessionLogIndex.Parse(new DateTime(2026, 10, 6), folder);

        parsed.Should().NotBeNull();
        parsed!.Value.Started.Should().Be(new DateTime(2026, 10, 6, hour, minute, 0));
        parsed.Value.RunType.Should().Be(runType);
    }

    [Theory]
    [InlineData("installs")]
    [InlineData("2560-boot")]
    [InlineData("ab")]
    public void NonSessionFolder_IsSkipped(string folder)
    {
        SessionLogIndex.Parse(new DateTime(2026, 10, 6), folder).Should().BeNull();
    }

    [Fact]
    public void List_IsNewestFirst_AndIgnoresOtherFolders()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "2026-10-05", "2300-boot"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "2026-10-06", "0900-login"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "2026-10-06", "1400-trigger"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "installs", "x"));

        SessionLogIndex.List(_temp.Path).Select(e => e.Folder)
            .Should().Equal("1400-trigger", "0900-login", "2300-boot");
    }

    [Fact]
    public void FindNew_ReturnsOnlyASessionStartedAfterTheSnapshot_OfTheRightType()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "2026-10-06", "1400-trigger"));
        var before = SessionLogIndex.Snapshot(_temp.Path);

        Directory.CreateDirectory(Path.Combine(_temp.Path, "2026-10-06", "1401-boot"));
        SessionLogIndex.FindNew(before, "trigger", _temp.Path).Should().BeNull();

        Directory.CreateDirectory(Path.Combine(_temp.Path, "2026-10-06", "1400-trigger_2"));
        SessionLogIndex.FindNew(before, "trigger", _temp.Path)!.Folder.Should().Be("1400-trigger_2");
    }

    [Fact]
    public void ReadStatus_ReadsSessionJson()
    {
        var dir = Path.Combine(_temp.Path, "2026-10-06", "1400-trigger");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "session.json"), """{ "session_id": "x", "status": "completed_with_errors" }""");

        var entry = SessionLogIndex.List(_temp.Path).Single();

        SessionLogIndex.ReadStatus(entry).Should().Be("completed_with_errors");
    }
}
