using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace StartSet.Infrastructure.Security;

/// <summary>
/// Whether a file a privileged StartSet process is about to act on could have been
/// written by someone who is not an administrator.
/// </summary>
/// <remarks>
/// The rule, in full:
///   1. A link is never trusted.
///   2. Every folder from the file up to the data root must be locked: no allow entry
///      gives anyone but SYSTEM, Administrators or TrustedInstaller a right to create,
///      delete, rewrite the ACL of, or take ownership of anything in it. In a locked
///      folder only an administrator can create a file, so a file there was written by
///      an administrator -- whichever account's SID owns it.
///   3. The file itself grants no such right to anyone else.
/// The owner SID is not part of the decision. An owner does hold an implicit WRITE_DAC,
/// so when this runs as SYSTEM it first gives an individually owned file to
/// BUILTIN\Administrators; files left over from before the folder was locked are
/// quarantined by <see cref="DataDirectoryGuard"/>, not judged here.
///
/// The decision itself is <see cref="FindNonAdminWriter"/>, a pure function, so the
/// same rule can be copied to another tool with the folder walk around it.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class FileTrust
{
    public const string SystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string UsersSid = "S-1-5-32-545";
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>
    /// Rights that let the holder change what a file or folder holds, or who controls it:
    /// write data / add file, append / add subfolder, delete child, delete, WRITE_DAC,
    /// WRITE_OWNER, and the generic write and all bits.
    /// </summary>
    public const int WriteMask =
        0x0002 | 0x0004 | 0x0040 | 0x10000 | 0x40000 | 0x80000 | 0x40000000 | 0x10000000;

    /// <summary>SYSTEM, Administrators or TrustedInstaller: the only principals allowed to write.</summary>
    public static bool IsAdministrativeSid(string? sid) =>
        sid is SystemSid or AdministratorsSid or TrustedInstallerSid;

    /// <summary>
    /// The first principal other than SYSTEM, Administrators or TrustedInstaller that an
    /// entry applying to the object lets write, or null when there is none. Deny entries
    /// are not credited: an allow is treated as granted.
    /// </summary>
    public static string? FindNonAdminWriter(IEnumerable<AccessEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!entry.Allow || entry.InheritOnly) continue;
            if ((entry.Rights & WriteMask) == 0) continue;
            if (IsAdministrativeSid(entry.Sid)) continue;
            return entry.Sid;
        }
        return null;
    }

    /// <summary>True when only SYSTEM, Administrators and TrustedInstaller can write in <paramref name="folder"/>.</summary>
    public static bool IsLocked(DirectoryInfo folder) =>
        FindNonAdminWriter(EntriesOf(folder.GetAccessControl())) is null;

    /// <summary>
    /// Checks <paramref name="path"/> against the rule above, walking its folders up to and
    /// including <paramref name="root"/>. A file outside <paramref name="root"/> has only its
    /// own folder checked.
    /// </summary>
    /// <param name="normalizeOwner">
    /// Give an individually owned file to BUILTIN\Administrators first. Pass true when the
    /// caller runs as SYSTEM.
    /// </param>
    public static TrustResult CheckFile(string path, string root, bool normalizeOwner)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                return TrustResult.Untrusted($"{path} does not exist");
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return TrustResult.Untrusted($"{path} is a link, not a file");

            foreach (var folder in FoldersUpTo(file.Directory, root))
            {
                if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return TrustResult.Untrusted($"{folder.FullName} is a link, not a folder");
                if (FindNonAdminWriter(EntriesOf(folder.GetAccessControl())) is { } folderWriter)
                    return TrustResult.Untrusted($"{folder.FullName} is not locked: {folderWriter} can write in it");
            }

            if (normalizeOwner)
                NormalizeOwner(file);

            if (FindNonAdminWriter(EntriesOf(file.GetAccessControl())) is { } writer)
                return TrustResult.Untrusted($"{path} is writable by {writer}");

            return TrustResult.Trusted;
        }
        catch (Exception ex)
        {
            return TrustResult.Untrusted($"{path} could not be checked: {ex.Message}");
        }
    }

    /// <summary>
    /// Gives <paramref name="file"/> to BUILTIN\Administrators when an individual account owns
    /// it, removing that account's implicit WRITE_DAC. Returns true when the owner changed.
    /// </summary>
    public static bool NormalizeOwner(FileSystemInfo file)
    {
        var current = OwnerOf(file.FullName);
        if (IsAdministrativeSid(current))
            return false;

        var security = file is DirectoryInfo ? (FileSystemSecurity)new DirectorySecurity() : new FileSecurity();
        security.SetOwner(new SecurityIdentifier(AdministratorsSid));
        if (file is DirectoryInfo dir) dir.SetAccessControl((DirectorySecurity)security);
        else ((FileInfo)file).SetAccessControl((FileSecurity)security);
        return true;
    }

    /// <summary>True when this process is LocalSystem.</summary>
    public static bool IsRunningAsSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value == SystemSid;
    }

    /// <summary>The owner of <paramref name="path"/>, or null when it cannot be read.</summary>
    public static string? OwnerOf(string path)
    {
        try
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            return (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The folder chain from <paramref name="start"/> up to and including <paramref name="root"/>.</summary>
    private static IEnumerable<DirectoryInfo> FoldersUpTo(DirectoryInfo? start, string root)
    {
        if (start is null) yield break;

        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var underRoot = Path.TrimEndingDirectorySeparator(start.FullName)
            .StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
        if (!underRoot)
        {
            yield return start;
            yield break;
        }

        for (var folder = start; folder is not null; folder = folder.Parent)
        {
            yield return folder;
            if (string.Equals(Path.TrimEndingDirectorySeparator(folder.FullName), rootFull, StringComparison.OrdinalIgnoreCase))
                yield break;
        }
    }

    private static IEnumerable<AccessEntry> EntriesOf(FileSystemSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => new AccessEntry(
                r.IdentityReference.Value,
                (int)r.FileSystemRights,
                r.AccessControlType == AccessControlType.Allow,
                r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)));
}

/// <summary>One access-control entry, reduced to what the trust decision needs.</summary>
public readonly record struct AccessEntry(string Sid, int Rights, bool Allow, bool InheritOnly = false);

/// <summary>Whether a file can be trusted and, when it cannot, why.</summary>
public readonly record struct TrustResult(bool IsTrusted, string? Reason)
{
    public static TrustResult Trusted => new(true, null);
    public static TrustResult Untrusted(string reason) => new(false, reason);
}
