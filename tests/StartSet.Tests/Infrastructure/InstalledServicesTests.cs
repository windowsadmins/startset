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
    [InlineData("Auto", false, "Starts automatically at boot")]
    [InlineData("Auto", true, "Starts automatically (delayed) at boot")]
    [InlineData("Boot", false, "Starts automatically at boot")]
    [InlineData("Manual", false, "Starts manually")]
    [InlineData("Manual", true, "Starts manually")]
    [InlineData("Disabled", false, "Disabled")]
    [InlineData(null, false, "Unknown")]
    [InlineData("Something", false, "Unknown")]
    public void StartMode_InWords(string? startMode, bool delayed, string expected)
    {
        InstalledServices.StartModeText(startMode, delayed).Should().Be(expected);
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
    [InlineData("Running", 0, "—")]
    [InlineData("Stopped", null, "—")]
    public void LastResult_IsTheExitCode_OnlyOnceStopped(string? state, int? exitCode, string expected)
    {
        InstalledServices.ServiceLastResultText(state, exitCode).Should().Be(expected);
    }

    [Fact]
    public void Query_ReturnsARow_ForEveryEntry_EvenWhenNotInstalled()
    {
        var rows = InstalledServices.Query();

        rows.Select(r => r.Name).Should().Equal(InstalledServices.Footprint.Select(e => e.Name));
        rows.Should().OnlyContain(r => !string.IsNullOrEmpty(r.State) && !string.IsNullOrEmpty(r.Trigger));
    }
}
