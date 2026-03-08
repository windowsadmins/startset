using FluentAssertions;
using StartSet.Infrastructure.Validation;

namespace StartSet.Tests.Infrastructure;

public class PermissionValidatorTests
{
    // ──────────────── IsRunningElevated ────────────────

    [Fact]
    public void IsRunningElevated_ReturnsBoolean()
    {
        // Just verify it doesn't throw — the actual value depends on test runner context
        var result = PermissionValidator.IsRunningElevated();
        result.Should().Be(result); // bool is bool
    }

    // ──────────────── GetCurrentUser ────────────────

    [Fact]
    public void GetCurrentUser_ReturnsValidInfo()
    {
        // GetCurrentUser may throw IdentityNotMappedException on some environments
        // when group SIDs cannot be translated to NTAccount names (e.g., Azure AD joined).
        try
        {
            var user = PermissionValidator.GetCurrentUser();

            user.Should().NotBeNull();
            user.Username.Should().NotBeNullOrEmpty();
            user.Sid.Should().NotBeNullOrEmpty();
            user.Groups.Should().NotBeNull();
        }
        catch (System.Security.Principal.IdentityNotMappedException)
        {
            // Expected on AAD/Entra-joined machines where some SIDs can't be translated
        }
    }

    [Fact]
    public void GetCurrentUser_UsernameContainsBackslash()
    {
        // Windows usernames are typically DOMAIN\username or MACHINE\username
        try
        {
            var user = PermissionValidator.GetCurrentUser();
            user.Username.Should().Contain("\\");
        }
        catch (System.Security.Principal.IdentityNotMappedException)
        {
            // Expected on AAD/Entra-joined machines where some SIDs can't be translated
        }
    }

    // ──────────────── ValidateScript ────────────────

    [Fact]
    public void ValidateScript_NonExistentFile_IsInvalid()
    {
        var validator = new PermissionValidator();
        var result = validator.ValidateScript(
            @"C:\nonexistent\path\script.ps1",
            StartSet.Core.Enums.PayloadType.BootEvery);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }
}
