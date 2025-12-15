using System.Security.AccessControl;
using System.Security.Principal;
using StartSet.Core.Enums;
using StartSet.Infrastructure.Logging;

namespace StartSet.Infrastructure.Validation;

/// <summary>
/// Service for validating script file permissions.
/// Ensures scripts have appropriate permissions before execution.
/// </summary>
public class PermissionValidator
{
    /// <summary>
    /// Validates that a script has appropriate permissions for execution.
    /// </summary>
    /// <param name="filePath">Path to the script file</param>
    /// <param name="payloadType">The payload type being executed</param>
    /// <returns>Validation result</returns>
    public PermissionValidationResult ValidateScript(string filePath, PayloadType payloadType)
    {
        var result = new PermissionValidationResult { FilePath = filePath };

        try
        {
            if (!File.Exists(filePath))
            {
                result.IsValid = false;
                result.Error = "File not found";
                return result;
            }

            var fileInfo = new FileInfo(filePath);
            var security = fileInfo.GetAccessControl();

            // Get owner
            var owner = security.GetOwner(typeof(NTAccount));
            result.Owner = owner?.ToString();

            // Get access rules
            var rules = security.GetAccessRules(true, true, typeof(NTAccount));
            result.AccessRules = rules.Cast<FileSystemAccessRule>()
                .Select(r => new AccessRuleInfo
                {
                    Identity = r.IdentityReference.Value,
                    AccessType = r.AccessControlType.ToString(),
                    Rights = r.FileSystemRights.ToString()
                })
                .ToList();

            // For elevated payloads, verify owner is admin or SYSTEM
            if (payloadType.RequiresElevation())
            {
                result.IsValid = IsOwnedByAdminOrSystem(security);
                if (!result.IsValid)
                {
                    result.Error = $"Elevated scripts must be owned by Administrator or SYSTEM. Current owner: {result.Owner}";
                    StartSetLogger.Warning("Permission validation failed for {FilePath}: {Error}", filePath, result.Error);
                }
            }
            else
            {
                // For user-context scripts, just verify file is readable
                result.IsValid = IsFileReadable(filePath);
                if (!result.IsValid)
                {
                    result.Error = "File is not readable";
                }
            }
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.Error = ex.Message;
            StartSetLogger.Error(ex, "Permission validation error for {FilePath}", filePath);
        }

        return result;
    }

    /// <summary>
    /// Checks if a file is owned by Administrator, Administrators group, or SYSTEM.
    /// </summary>
    private static bool IsOwnedByAdminOrSystem(FileSecurity security)
    {
        try
        {
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null) return false;

            // Check for SYSTEM (S-1-5-18)
            if (owner.Value == "S-1-5-18") return true;

            // Check for Administrator (S-1-5-21-*-500)
            if (owner.Value.EndsWith("-500")) return true;

            // Check for Administrators group (S-1-5-32-544)
            if (owner.Value == "S-1-5-32-544") return true;

            // Check for TrustedInstaller (S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)
            if (owner.Value.StartsWith("S-1-5-80-")) return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a file is readable.
    /// </summary>
    private static bool IsFileReadable(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies the current process is running elevated.
    /// </summary>
    public static bool IsRunningElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Gets the current user's identity information.
    /// </summary>
    public static UserInfo GetCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);

        return new UserInfo
        {
            Username = identity.Name,
            Sid = identity.User?.Value,
            IsElevated = principal.IsInRole(WindowsBuiltInRole.Administrator),
            IsSystem = identity.IsSystem,
            Groups = identity.Groups?
                .Select(g => g.Translate(typeof(NTAccount))?.Value ?? g.Value)
                .ToList() ?? []
        };
    }
}

/// <summary>
/// Result of permission validation.
/// </summary>
public class PermissionValidationResult
{
    public required string FilePath { get; set; }
    public bool IsValid { get; set; }
    public string? Error { get; set; }
    public string? Owner { get; set; }
    public List<AccessRuleInfo> AccessRules { get; set; } = [];
}

/// <summary>
/// Information about a file access rule.
/// </summary>
public class AccessRuleInfo
{
    public required string Identity { get; set; }
    public required string AccessType { get; set; }
    public required string Rights { get; set; }
}

/// <summary>
/// Information about the current user.
/// </summary>
public class UserInfo
{
    public required string Username { get; set; }
    public string? Sid { get; set; }
    public bool IsElevated { get; set; }
    public bool IsSystem { get; set; }
    public List<string> Groups { get; set; } = [];
}
