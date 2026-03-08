using FluentAssertions;
using StartSet.Core.Enums;
using StartSet.Core.Models;

namespace StartSet.Tests.Core;

public class ScriptPayloadTests
{
    [Fact]
    public void FileName_DerivedFromFilePath()
    {
        var payload = new ScriptPayload
        {
            FilePath = @"C:\ProgramData\ManagedState\boot-every\deploy.ps1",
            PayloadType = PayloadType.BootEvery
        };

        payload.FileName.Should().Be("deploy.ps1");
    }

    [Fact]
    public void Extension_IsLowercase()
    {
        var payload = new ScriptPayload
        {
            FilePath = @"C:\scripts\Install.PS1",
            PayloadType = PayloadType.BootEvery
        };

        payload.Extension.Should().Be(".ps1");
    }

    [Theory]
    [InlineData(@"C:\scripts\test.ps1", ".ps1")]
    [InlineData(@"C:\scripts\test.cmd", ".cmd")]
    [InlineData(@"C:\scripts\test.bat", ".bat")]
    [InlineData(@"C:\scripts\test.exe", ".exe")]
    [InlineData(@"C:\scripts\test.msi", ".msi")]
    [InlineData(@"C:\scripts\test.msix", ".msix")]
    public void Extension_MatchesExpected(string filePath, string expectedExtension)
    {
        var payload = new ScriptPayload
        {
            FilePath = filePath,
            PayloadType = PayloadType.BootEvery
        };

        payload.Extension.Should().Be(expectedExtension);
    }

    [Fact]
    public void DefaultProperties_AreInitialized()
    {
        var payload = new ScriptPayload
        {
            FilePath = @"C:\scripts\test.ps1",
            PayloadType = PayloadType.LoginOnce
        };

        payload.ShouldSkip.Should().BeFalse();
        payload.SkipReason.Should().BeNull();
        payload.Checksum.Should().BeNull();
        payload.SortOrder.Should().Be(0);
    }

    [Fact]
    public void ShouldSkip_CanBeSet()
    {
        var payload = new ScriptPayload
        {
            FilePath = @"C:\scripts\test.ps1",
            PayloadType = PayloadType.LoginOnce,
            ShouldSkip = true,
            SkipReason = "Permission denied"
        };

        payload.ShouldSkip.Should().BeTrue();
        payload.SkipReason.Should().Be("Permission denied");
    }
}
