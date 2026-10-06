using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace StartSet.Infrastructure.Security;

/// <summary>
/// Whether a file a privileged StartSet process is about to act on could have been
/// written by someone who is not an administrator.
/// </summary>
/// <remarks>
/// The service runs as SYSTEM and executes payloads, and reads Config.yaml, straight
/// from ProgramData. Anything there that a standard user could change is a way for that
/// user to have SYSTEM -- or another user's session -- run what they wrote. So before a
/// file is trusted three things are checked: it is not a link; its owner is SYSTEM,
/// Administrators, TrustedInstaller or the built-in Administrator (an owner can always
/// rewrite the ACL); and no allow entry on the file, or on the folder holding it, gives
/// anyone else a right that changes content, replaces the file or rewrites its security.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class FileTrust
{
    public const string SystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string UsersSid = "S-1-5-32-545";
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>
    /// Rights that let the holder change what the file or folder holds, or who controls it:
    /// write/add-file, append/add-subfolder, delete-child, delete, write-DAC, write-owner,
    /// and the generic write and all bits.
    /// </summary>
    internal const int WriteMask =
        0x0002 | 0x0004 | 0x0040 | 0x10000 | 0x40000 | 0x80000 | 0x40000000 | 0x10000000;

    /// <summary>SYSTEM, Administrators, TrustedInstaller, or a machine's built-in Administrator account.</summary>
    public static bool IsAdministrativeSid(string? sid) =>
        sid is not null &&
        (sid == SystemSid ||
         sid == AdministratorsSid ||
         sid == TrustedInstallerSid ||
         (sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && sid.EndsWith("-500", StringComparison.Ordinal)));

    /// <summary>
    /// The decision on its own, so it can be tested without real ACLs: trusted when the owner
    /// is administrative and no entry that applies to the object grants a non-administrator a
    /// write right. Deny entries are not credited -- an allow is treated as granted.
    /// </summary>
    public static TrustResult Evaluate(string? ownerSid, IEnumerable<AccessEntry> entries, string what)
    {
        if (!IsAdministrativeSid(ownerSid))
            return TrustResult.Untrusted($"{what} is owned by {ownerSid ?? "an unknown owner"}, not by an administrator");

        foreach (var entry in entries)
        {
            if (!entry.Allow || entry.InheritOnly) continue;
            if ((entry.Rights & WriteMask) == 0) continue;
            if (IsAdministrativeSid(entry.Sid)) continue;
            return TrustResult.Untrusted($"{what} is writable by {entry.Sid}");
        }

        return TrustResult.Trusted;
    }

    /// <summary>
    /// Checks <paramref name="path"/> and the folder that holds it. Anyone who can create or
    /// delete entries in that folder can replace the file, so the folder counts too.
    /// </summary>
    public static TrustResult CheckFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                return TrustResult.Untrusted($"{path} does not exist");
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return TrustResult.Untrusted($"{path} is a link, not a file");

            var fileResult = Evaluate(file.GetAccessControl(), path);
            if (!fileResult.IsTrusted)
                return fileResult;

            var folder = file.Directory;
            if (folder is null)
                return TrustResult.Trusted;
            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return TrustResult.Untrusted($"{folder.FullName} is a link, not a folder");

            return Evaluate(folder.GetAccessControl(), folder.FullName);
        }
        catch (Exception ex)
        {
            return TrustResult.Untrusted($"{path} could not be checked: {ex.Message}");
        }
    }

    /// <summary>The owner of <paramref name="path"/>, or null when it cannot be read.</summary>
    public static string? OwnerOf(string path)
    {
        try
        {
            var security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            return (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static TrustResult Evaluate(FileSystemSecurity security, string what)
    {
        var owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        var entries = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => new AccessEntry(
                r.IdentityReference.Value,
                (int)r.FileSystemRights,
                r.AccessControlType == AccessControlType.Allow,
                r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)));
        return Evaluate(owner, entries, what);
    }
}

/// <summary>One access-control entry, reduced to what the trust decision needs.</summary>
public readonly record struct AccessEntry(string Sid, int Rights, bool Allow, bool InheritOnly = false);

/// <summary>Whether a file can be trusted and, when it cannot, why.</summary>
public readonly record struct TrustResult(bool IsTrusted, string? Reason)
{
    public static TrustResult Trusted => new(true, null);
    public static TrustResult Untrusted(string reason) => new(false, reason);
}
