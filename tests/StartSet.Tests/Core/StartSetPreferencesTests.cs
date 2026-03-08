using FluentAssertions;
using StartSet.Core.Models;

namespace StartSet.Tests.Core;

public class StartSetPreferencesTests
{
    [Fact]
    public void Default_ReturnsNewInstance()
    {
        var a = StartSetPreferences.Default;
        var b = StartSetPreferences.Default;

        // Default should return new instances (not mutable singletons)
        a.Should().NotBeSameAs(b);
    }

    [Fact]
    public void Default_HasExpectedValues()
    {
        var prefs = StartSetPreferences.Default;

        prefs.WaitForNetwork.Should().BeTrue();
        prefs.NetworkTimeout.Should().Be(180);
        prefs.IgnoreNetworkFailure.Should().BeFalse();
        prefs.Verbose.Should().BeFalse();
        prefs.Debug.Should().BeFalse();
        prefs.ScriptTimeout.Should().Be(3600);
        prefs.ParallelExecution.Should().BeFalse();
        prefs.LoginDelay.Should().Be(0);
        prefs.LogScriptOutput.Should().BeTrue();
        prefs.ChecksumValidation.Should().BeFalse();
    }

    [Fact]
    public void Default_AllowedExtensions_ContainsStandardTypes()
    {
        var prefs = StartSetPreferences.Default;

        prefs.AllowedExtensions.Should().Contain(".ps1");
        prefs.AllowedExtensions.Should().Contain(".cmd");
        prefs.AllowedExtensions.Should().Contain(".bat");
        prefs.AllowedExtensions.Should().Contain(".exe");
        prefs.AllowedExtensions.Should().Contain(".msi");
    }

    [Fact]
    public void Default_IgnoredUsers_IsEmpty()
    {
        StartSetPreferences.Default.IgnoredUsers.Should().BeEmpty();
    }

    [Fact]
    public void Default_Overrides_IsEmpty()
    {
        StartSetPreferences.Default.Overrides.Should().BeEmpty();
    }
}
