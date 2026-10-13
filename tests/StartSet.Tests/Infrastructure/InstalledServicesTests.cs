using FluentAssertions;
using StartSet.Infrastructure.Gui;

namespace StartSet.Tests.Infrastructure;

/// <summary>The plain words on the Prefs tab's Services card.</summary>
public class InstalledServicesTests
{
    [Fact]
    public void Footprint_IsTheOneServiceTheInstallerCreates()
    {
        InstalledServices.Footprint.Should().ContainSingle()
            .Which.Should().Match<FootprintEntry>(e => e.Name == "StartSet" && e.Kind == InstalledServices.ServiceKind);
    }

    [Theory]
    [InlineData("Auto", false, "Automatic")]
    [InlineData("Auto", true, "Automatic (delayed)")]
    [InlineData("Boot", false, "Automatic")]
    [InlineData("Manual", false, "Manual")]
    [InlineData("Manual", true, "Manual")]
    [InlineData("Disabled", false, "Disabled")]
    [InlineData(null, false, null)]
    [InlineData("Something", false, null)]
    public void StartType_InWords(string? startMode, bool delayed, string? expected)
    {
        InstalledServices.StartTypeText(startMode, delayed).Should().Be(expected);
    }

    [Theory]
    [InlineData("Auto", false, "At boot")]
    [InlineData("Auto", true, "Shortly after boot (delayed start)")]
    [InlineData("Manual", false, "When started by a user or another program")]
    [InlineData("Disabled", false, "None: the service is disabled")]
    [InlineData(null, false, null)]
    public void Triggers_FollowTheStartType(string? startMode, bool delayed, string? expected)
    {
        InstalledServices.ServiceTriggerText(startMode, delayed).Should().Be(expected);
    }

    [Theory]
    [InlineData("Running", "Auto", "Running")]
    [InlineData("Stopped", "Manual", "Stopped")]
    [InlineData("Stopped", "Disabled", "Disabled")]
    [InlineData("Running", "Disabled", "Disabled")]
    [InlineData("Start Pending", "Auto", "Starting")]
    [InlineData("Stop Pending", "Auto", "Stopping")]
    [InlineData(null, "Auto", "Unknown")]
    public void State_InWords(string? state, string? startMode, string expected)
    {
        InstalledServices.ServiceStateText(state, startMode).Should().Be(expected);
    }

    [Theory]
    [InlineData("Stopped", 0, "Exit code 0")]
    [InlineData("Stopped", 1067, "Exit code 1067")]
    [InlineData("Running", 0, null)]
    [InlineData("Stopped", null, null)]
    public void LastResult_IsTheExitCode_OnlyOnceStopped(string? state, int? exitCode, string? expected)
    {
        InstalledServices.ServiceLastResultText(state, exitCode).Should().Be(expected);
    }

    [Fact]
    public void RunningService_ShowsItsRows_InTheFixedOrder_WithoutALastResult()
    {
        var rows = InstalledServices.ServiceDetails("StartSet", "Running", "Auto", delayed: false, "Running", exitCode: 0,
            account: "LocalSystem", path: @"""C:\Program Files\StartSet\StartSetService.exe""");

        rows.Select(r => r.Label).Should().Equal("Name", "Start type", "Status", "Account", "Path and arguments", "Triggers");
        rows.Select(r => r.Value).Should().Equal("StartSet", "Automatic", "Running", "LocalSystem",
            @"""C:\Program Files\StartSet\StartSetService.exe""", "At boot");
    }

    [Fact]
    public void StoppedService_AddsItsExitCode_AndMissingValues_AreLeftOut()
    {
        var rows = InstalledServices.ServiceDetails("StartSet", "Stopped", "Manual", delayed: false, "Stopped", exitCode: 1067,
            account: null, path: "  ");

        rows.Select(r => r.Label).Should().Equal("Name", "Start type", "Status", "Triggers", "Last result");
        rows.Last().Value.Should().Be("Exit code 1067");
    }

    [Theory]
    [InlineData("Auto", false, "LocalSystem", "Service · Automatic · LocalSystem")]
    [InlineData("Auto", true, @"NT AUTHORITY\LocalService", @"Service · Automatic (delayed) · NT AUTHORITY\LocalService")]
    [InlineData(null, false, null, "Service")]
    public void Summary_IsKind_StartType_AndAccount(string? startMode, bool delayed, string? account, string expected)
    {
        InstalledServices.ServiceSummary(startMode, delayed, account).Should().Be(expected);
    }

    [Fact]
    public void Query_ReturnsARow_ForEveryEntry_EvenWhenNotInstalled()
    {
        var rows = InstalledServices.Query();

        rows.Select(r => r.Name).Should().Equal(InstalledServices.Footprint.Select(e => e.Name));
        rows.Should().OnlyContain(r => !string.IsNullOrEmpty(r.State) && r.Details.Any(d => d.Label == "Status"));
        rows[0].Summary.Should().StartWith("Service");
    }
}
