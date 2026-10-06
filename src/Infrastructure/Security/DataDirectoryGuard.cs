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
/// created there inherits that. At start-up the service resets the root to SYSTEM and
/// Administrators full control and Users read, not inherited from ProgramData; puts every
/// standard folder below it back on that inherited ACL; and replaces any of those folders
/// that is a link. The one exception is the triggers folder, where Users may also create
/// files -- a trigger file is a request to run, not something StartSet reads or executes.
///
/// It does not delete payloads. A file a non-administrator could write is skipped when a
/// run reaches it, and the run log says why, so an administrator can fix it rather than
/// find it gone. The MSI and the package postinstall set the same ACL when they install.
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

    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>
    /// Locks <paramref name="root"/> down and returns one line per thing it changed or could not
    /// fix, for the caller to log. Must run as SYSTEM or elevated.
    /// </summary>
    /// <param name="root">The data root; the installed location when null.</param>
    public static List<string> Secure(string? root = null)
    {
        var target = root ?? Paths.ScriptRoot;
        var notes = new List<string>();

        try
        {
            RemoveIfLink(target, notes);
            Directory.CreateDirectory(target);
            new DirectoryInfo(target).SetAccessControl(Locked(allowUserTriggers: false, notes));

            foreach (var installed in Paths.AllPayloadDirectories)
            {
                var folder = Path.Combine(target, Path.GetRelativePath(Paths.ScriptRoot, installed));
                var isTriggers = string.Equals(installed, Paths.TriggerDirectory, StringComparison.OrdinalIgnoreCase);
                try
                {
                    RemoveIfLink(folder, notes);
                    Directory.CreateDirectory(folder);
                    var info = new DirectoryInfo(folder);
                    info.SetAccessControl(isTriggers ? Locked(allowUserTriggers: true, notes) : Inherited(notes));
                }
                catch (Exception ex)
                {
                    notes.Add($"Could not secure {folder}: {ex.Message}");
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
    /// SYSTEM and Administrators full control, Users read, inherited by everything below and
    /// not inheriting from ProgramData. Users may also create files in the triggers folder.
    /// </summary>
    internal static DirectorySecurity Locked(bool allowUserTriggers, List<string> notes)
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
        SetOwner(security, notes);
        return security;
    }

    /// <summary>No explicit entries; the folder takes the root's ACL.</summary>
    private static DirectorySecurity Inherited(List<string> notes)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        SetOwner(security, notes);
        return security;
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
