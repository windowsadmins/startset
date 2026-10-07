using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace StartSet.Infrastructure.Security;

/// <summary>
/// Whether an owner SID can be resolved to an administrator: SYSTEM, Administrators,
/// TrustedInstaller, or a direct member of the local Administrators group (which is where
/// a local admin account, a domain admin group or a cloud admin role appears).
/// </summary>
[SupportedOSPlatform("windows")]
public static class LocalAdministrators
{
    public static bool IsAdministrator(string? sid)
    {
        if (sid is null) return false;
        if (FileTrust.IsAdministrativeSid(sid)) return true;
        try
        {
            return Members().Contains(sid);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>SIDs that are direct members of the local Administrators group.</summary>
    public static HashSet<string> Members()
    {
        var group = new SecurityIdentifier(FileTrust.AdministratorsSid).Translate(typeof(NTAccount)).Value;
        var name = group.Contains('\\') ? group[(group.IndexOf('\\') + 1)..] : group;
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var status = NetLocalGroupGetMembers(null, name, 0, out var buffer, -1, out var read, out _, IntPtr.Zero);
        try
        {
            if (status != 0)
                throw new InvalidOperationException($"NetLocalGroupGetMembers failed: {status}");
            var size = Marshal.SizeOf<LOCALGROUP_MEMBERS_INFO_0>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<LOCALGROUP_MEMBERS_INFO_0>(buffer + i * size);
                members.Add(new SecurityIdentifier(info.lgrmi0_sid).Value);
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
        return members;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LOCALGROUP_MEMBERS_INFO_0 { public IntPtr lgrmi0_sid; }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? server, string group, int level, out IntPtr buffer,
        int prefMaxLen, out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
