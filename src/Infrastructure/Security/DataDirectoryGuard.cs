using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using StartSet.Core.Constants;

namespace StartSet.Infrastructure.Security;

/// <summary>
/// Keeps C:\ProgramData\ManagedState for administrators. The service runs as SYSTEM and
/// executes what it finds there, so nothing in it may come from a standard user.
/// </summary>
/// <remarks>
/// ProgramData's default ACL lets any user create files and folders below it, and a folder
/// created there inherits that. At start-up the service, for the root and each standard
/// folder below it:
///   1. Replaces the folder if it is a link.
///   2. Notes whether the folder was already locked.
///   3. If it was not, quarantines every file in it whose owner cannot be resolved to an
///      administrator: until now a standard user could have written it. Each file is moved
///      to ManagedState\quarantine\&lt;timestamp&gt;\ and logged, never deleted. This happens
///      once -- the first time a folder is locked -- because after that only an
///      administrator can create a file there.
///   4. Locks it: the root gets SYSTEM and Administrators full control and Users read, not
///      inherited from ProgramData; every folder below takes that by inheritance. The
///      triggers folder also lets Users create files, since a trigger file is a request to
///      run, not something StartSet reads or executes.
///   5. Gives each remaining individually owned file to BUILTIN\Administrators, so no
///      account keeps the implicit WRITE_DAC an owner holds.
/// The MSI and the package postinstall set the same ACL when they install.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class DataDirectoryGuard
{
    /// <summary>The ACL the installers apply to the root, as SDDL.</summary>
    public const string RootSddl = "O:SYG:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

    /// <summary>
    /// The triggers folder: the root's ACL plus Users "create files" on this folder only.
    /// </summary>
    public const string TriggerSddl = "O:SYG:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;;0x100002;;;BU)";

    /// <summary>Where files set aside by the first lock go, under the data root.</summary>
    public const string QuarantineFolderName = "quarantine";

    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>
    /// Folders whose files StartSet runs or reads as settings: the payload folders. Logs,
    /// reports, share and triggers hold StartSet's own output or requests and are not
    /// quarantined.
    /// </summary>
    private static readonly string[] ExecutedFolders = Paths.AllPayloadDirectories
        .Where(d => d != Paths.ShareDir && d != Paths.LogDirectory && d != Paths.ReportsDirectory && d != Paths.TriggerDirectory)
        .ToArray();

    /// <summary>
    /// Locks <paramref name="root"/> down and returns one line per thing it changed or could not
    /// fix, for the caller to log. Must run as SYSTEM or elevated.
    /// </summary>
    /// <param name="root">The data root; the installed location when null.</param>
    /// <param name="isAdministrator">
    /// Resolves an owner SID to an administrator; <see cref="LocalAdministrators.IsAdministrator"/>
    /// when null.
    /// </param>
    public static List<string> Secure(string? root = null, Func<string?, bool>? isAdministrator = null)
    {
        var target = root ?? Paths.ScriptRoot;
        isAdministrator ??= LocalAdministrators.IsAdministrator;
        var notes = new List<string>();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        try
        {
            // 1. Replace links, create the folders, and quarantine what was written while a
            //    folder was still open -- all before anything is locked, so the moves are
            //    not blocked by the new ACL.
            RemoveIfLink(target, notes);
            Directory.CreateDirectory(target);
            var rootInfo = new DirectoryInfo(target);
            var rootWasLocked = FileTrust.IsLocked(rootInfo);
            if (!rootWasLocked)
                Quarantine(rootInfo, target, stamp, isAdministrator, notes);

            var folders = new List<(DirectoryInfo Info, bool IsTriggers, bool IsExecuted)>();
            foreach (var installed in Paths.AllPayloadDirectories)
            {
                var folder = Path.Combine(target, Path.GetRelativePath(Paths.ScriptRoot, installed));
                var isTriggers = string.Equals(installed, Paths.TriggerDirectory, StringComparison.OrdinalIgnoreCase);
                var isExecuted = ExecutedFolders.Contains(installed, StringComparer.OrdinalIgnoreCase);
                try
                {
                    RemoveIfLink(folder, notes);
                    Directory.CreateDirectory(folder);
                    var info = new DirectoryInfo(folder);

                    // A folder below an unlocked root was writable by users too, whatever
                    // its own entries said.
                    if (isExecuted && (!rootWasLocked || !FileTrust.IsLocked(info)))
                        Quarantine(info, target, stamp, isAdministrator, notes);

                    folders.Add((info, isTriggers, isExecuted));
                }
                catch (Exception ex)
                {
                    notes.Add($"Could not prepare {folder}: {ex.Message}");
                }
            }

            // 2. Lock the root, then put each folder on it (triggers keeps its extra entry).
            Apply(rootInfo, withOwner => Locked(allowUserTriggers: false, notes, withOwner), notes);
            NormalizeOwners(rootInfo, notes);

            foreach (var (info, isTriggers, isExecuted) in folders)
            {
                try
                {
                    Apply(info, withOwner => isTriggers ? Locked(allowUserTriggers: true, notes, withOwner) : Inherited(notes, withOwner), notes);

                    // 3. Take the implicit WRITE_DAC away from any individual owner.
                    if (isExecuted)
                        NormalizeOwners(info, notes);
                }
                catch (Exception ex)
                {
                    notes.Add($"Could not secure {info.FullName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            notes.Add($"Could not secure {target}: {ex.Message}");
        }

        return notes;
    }

    /// <summary>
    /// Moves each file in <paramref name="folder"/> whose owner is not an administrator to
    /// quarantine\&lt;stamp&gt;\, keeping its path relative to the data root. Links are moved
    /// too: they are never trusted.
    /// </summary>
    private static void Quarantine(DirectoryInfo folder, string root, string stamp, Func<string?, bool> isAdministrator, List<string> notes)
    {
        foreach (var file in folder.EnumerateFiles())
        {
            try
            {
                var owner = FileTrust.OwnerOf(file.FullName);
                var link = file.Attributes.HasFlag(FileAttributes.ReparsePoint);
                if (!link && isAdministrator(owner))
                    continue;

                var relative = Path.GetRelativePath(root, file.FullName);
                var destination = Path.Combine(root, QuarantineFolderName, stamp, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                file.MoveTo(destination);
                notes.Add(link
                    ? $"Quarantined {relative} to {destination}: it is a link"
                    : $"Quarantined {relative} to {destination}: it was there before the folder was locked, and its owner {owner ?? "(unknown)"} is not an administrator");
            }
            catch (Exception ex)
            {
                notes.Add($"Could not quarantine {file.FullName}: {ex.Message}");
            }
        }
    }

    /// <summary>Gives each individually owned file in <paramref name="folder"/> to BUILTIN\Administrators.</summary>
    private static void NormalizeOwners(DirectoryInfo folder, List<string> notes)
    {
        foreach (var file in folder.EnumerateFiles())
        {
            try
            {
                var before = FileTrust.OwnerOf(file.FullName);
                if (FileTrust.NormalizeOwner(file))
                    notes.Add($"Gave {file.FullName} to Administrators (was owned by {before ?? "an unknown owner"})");
            }
            catch (Exception ex)
            {
                notes.Add($"Could not change the owner of {file.FullName}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// SYSTEM and Administrators full control, Users read, inherited by everything below and
    /// not inheriting from ProgramData. Users may also create files in the triggers folder.
    /// </summary>
    internal static DirectorySecurity Locked(bool allowUserTriggers, List<string> notes, bool withOwner = true)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        if (allowUserTriggers)
        {
            security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.CreateFiles | FileSystemRights.Synchronize,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        }
        if (withOwner) SetOwner(security, notes);
        return security;
    }

    /// <summary>No explicit entries; the folder takes the root's ACL.</summary>
    private static DirectorySecurity Inherited(List<string> notes, bool withOwner = true)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        if (withOwner) SetOwner(security, notes);
        return security;
    }

    /// <summary>
    /// Applies the ACL with its owner. A process that may not assign that owner -- not
    /// SYSTEM, not elevated -- still applies the ACL, and the note says the owner stayed.
    /// </summary>
    private static void Apply(DirectoryInfo folder, Func<bool, DirectorySecurity> build, List<string> notes)
    {
        try
        {
            folder.SetAccessControl(build(true));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IdentityNotMappedException)
        {
            notes.Add($"Owner of {folder.FullName} not changed: {ex.Message}");
            folder.SetAccessControl(build(false));
        }
    }

    /// <summary>
    /// SYSTEM when this runs as SYSTEM; an elevated administrator may only assign the
    /// Administrators group. Whoever owns a folder can rewrite its ACL.
    /// </summary>
    private static void SetOwner(DirectorySecurity security, List<string> notes)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            security.SetOwner(identity.User == System ? System : Administrators);
        }
        catch (Exception ex)
        {
            notes.Add($"Owner not changed: {ex.Message}");
        }
    }

    /// <summary>A link in place of a folder would send every read and write somewhere else.</summary>
    private static void RemoveIfLink(string path, List<string> notes)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(path);
            notes.Add($"Removed {path}: it was a link, not a folder");
        }
        else if (!info.Exists && File.Exists(path))
        {
            File.Delete(path);
            notes.Add($"Removed {path}: it was a file, not a folder");
        }
    }
}
