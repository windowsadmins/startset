using FluentAssertions;
using StartSet.Core.Constants;
using StartSet.Core.Enums;

namespace StartSet.Tests.Core;

public class PayloadTypeExtensionsTests
{
    // ──────────────── GetDirectoryPath ────────────────

    [Theory]
    [InlineData(PayloadType.BootOnce, Paths.BootOnceDir)]
    [InlineData(PayloadType.BootEvery, Paths.BootEveryDir)]
    [InlineData(PayloadType.LoginWindow, Paths.LoginWindowDir)]
    [InlineData(PayloadType.LoginOnce, Paths.LoginOnceDir)]
    [InlineData(PayloadType.LoginEvery, Paths.LoginEveryDir)]
    [InlineData(PayloadType.LoginPrivilegedOnce, Paths.LoginPrivilegedOnceDir)]
    [InlineData(PayloadType.LoginPrivilegedEvery, Paths.LoginPrivilegedEveryDir)]
    [InlineData(PayloadType.OnDemand, Paths.OnDemandDir)]
    [InlineData(PayloadType.OnDemandPrivileged, Paths.OnDemandPrivilegedDir)]
    public void GetDirectoryPath_ReturnsCorrectPath(PayloadType payloadType, string expected)
    {
        payloadType.GetDirectoryPath().Should().Be(expected);
    }

    [Fact]
    public void GetDirectoryPath_AllEnumValues_AreMapped()
    {
        // Every enum value should map to a directory without throwing
        foreach (var value in Enum.GetValues<PayloadType>())
        {
            var act = () => value.GetDirectoryPath();
            act.Should().NotThrow($"PayloadType.{value} should have a directory mapping");
        }
    }

    // ──────────────── IsRunOnce ────────────────

    [Theory]
    [InlineData(PayloadType.BootOnce, true)]
    [InlineData(PayloadType.LoginOnce, true)]
    [InlineData(PayloadType.LoginPrivilegedOnce, true)]
    [InlineData(PayloadType.BootEvery, false)]
    [InlineData(PayloadType.LoginEvery, false)]
    [InlineData(PayloadType.LoginPrivilegedEvery, false)]
    [InlineData(PayloadType.LoginWindow, false)]
    [InlineData(PayloadType.OnDemand, false)]
    [InlineData(PayloadType.OnDemandPrivileged, false)]
    public void IsRunOnce_ReturnsExpected(PayloadType payloadType, bool expected)
    {
        payloadType.IsRunOnce().Should().Be(expected);
    }

    // ──────────────── RequiresElevation ────────────────

    [Theory]
    [InlineData(PayloadType.BootOnce, true)]
    [InlineData(PayloadType.BootEvery, true)]
    [InlineData(PayloadType.LoginWindow, true)]
    [InlineData(PayloadType.LoginPrivilegedOnce, true)]
    [InlineData(PayloadType.LoginPrivilegedEvery, true)]
    [InlineData(PayloadType.OnDemandPrivileged, true)]
    [InlineData(PayloadType.LoginOnce, false)]
    [InlineData(PayloadType.LoginEvery, false)]
    [InlineData(PayloadType.OnDemand, false)]
    public void RequiresElevation_ReturnsExpected(PayloadType payloadType, bool expected)
    {
        payloadType.RequiresElevation().Should().Be(expected);
    }

    // ──────────────── IsUserContext ────────────────

    [Theory]
    [InlineData(PayloadType.LoginOnce, true)]
    [InlineData(PayloadType.LoginEvery, true)]
    [InlineData(PayloadType.OnDemand, true)]
    [InlineData(PayloadType.BootOnce, false)]
    [InlineData(PayloadType.BootEvery, false)]
    [InlineData(PayloadType.LoginWindow, false)]
    [InlineData(PayloadType.LoginPrivilegedOnce, false)]
    [InlineData(PayloadType.LoginPrivilegedEvery, false)]
    [InlineData(PayloadType.OnDemandPrivileged, false)]
    public void IsUserContext_ReturnsExpected(PayloadType payloadType, bool expected)
    {
        payloadType.IsUserContext().Should().Be(expected);
    }

    // ──────────────── DeleteAfterExecution ────────────────

    [Fact]
    public void DeleteAfterExecution_OnlyBootOnce()
    {
        PayloadType.BootOnce.DeleteAfterExecution().Should().BeTrue();

        // All other types should return false
        foreach (var value in Enum.GetValues<PayloadType>())
        {
            if (value != PayloadType.BootOnce)
                value.DeleteAfterExecution().Should().BeFalse($"PayloadType.{value} should not delete after execution");
        }
    }

    // ──────────────── Consistency checks ────────────────

    [Fact]
    public void RunOnce_Types_AreSubsetOf_AllTypes()
    {
        // Run-once types should be a strict subset — sanity check
        var runOnceTypes = Enum.GetValues<PayloadType>().Where(p => p.IsRunOnce()).ToList();
        runOnceTypes.Should().HaveCountGreaterThan(0);
        runOnceTypes.Should().OnlyContain(p => p.ToString().Contains("Once"));
    }

    [Fact]
    public void UserContext_Types_NeverRequireElevation()
    {
        // User-context types should not require elevation
        foreach (var value in Enum.GetValues<PayloadType>())
        {
            if (value.IsUserContext())
                value.RequiresElevation().Should().BeFalse(
                    $"PayloadType.{value} is user-context but claims to require elevation");
        }
    }
}
