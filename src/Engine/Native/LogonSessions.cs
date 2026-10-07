using System.Runtime.InteropServices;

namespace StartSet.Engine.Native;

/// <summary>
/// What Windows knows about a logon session and the desktop session it belongs to.
/// </summary>
/// <remarks>
/// Event 4624 names the logon session (TargetLogonId) but not the desktop session it
/// is in. The login worker used to assume the console session for every event, so an
/// interactive-type logon made anywhere on the machine -- a scheduled task, a service
/// calling LogonUser, a runas -- was treated as a user arriving at the desktop.
/// </remarks>
public static class LogonSessions
{
    /// <summary>The logon session is gone: it was closed before it could be looked up.</summary>
    public const int NoSuchLogonSession = -2;

    /// <summary>
    /// The desktop session the logon session <paramref name="logonId"/> belongs to,
    /// <see cref="NoSuchLogonSession"/> when it has already ended, or -1 when it could
    /// not be looked up.
    /// </summary>
    public static int GetSessionIdOfLogon(ulong logonId)
    {
        var luid = new Luid { LowPart = (uint)(logonId & 0xFFFFFFFF), HighPart = (int)(logonId >> 32) };
        var data = IntPtr.Zero;
        try
        {
            var status = LsaGetLogonSessionData(ref luid, out data);
            if (status == StatusNoSuchLogonSession) return NoSuchLogonSession;
            if (status != 0 || data == IntPtr.Zero) return -1;
            return (int)Marshal.PtrToStructure<SecurityLogonSessionData>(data).Session;
        }
        catch
        {
            return -1;
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                try { LsaFreeReturnBuffer(data); } catch { }
            }
        }
    }

    /// <summary>
    /// When the user now in <paramref name="sessionId"/> signed in to it, or null when
    /// nobody is signed in or it could not be read. A new sign-in to a session always
    /// has a new logon time, so this identifies one desktop logon.
    /// </summary>
    public static DateTime? GetSessionLogonTime(int sessionId)
    {
        if (sessionId < 0) return null;

        var buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsSessionInfo, out buffer, out _) || buffer == IntPtr.Zero)
                return null;

            var info = Marshal.PtrToStructure<WtsInfo>(buffer);
            return info.LogonTime > 0 ? DateTime.FromFileTimeUtc(info.LogonTime) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                try { WTSFreeMemory(buffer); } catch { }
            }
        }
    }

    private const uint StatusNoSuchLogonSession = 0xC000005F;
    private const int WtsSessionInfo = 24;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    // Only the leading fields are read; the rest of the structure is never touched.
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityLogonSessionData
    {
        public uint Size;
        public Luid LogonId;
        public LsaUnicodeString UserName;
        public LsaUnicodeString LogonDomain;
        public LsaUnicodeString AuthenticationPackage;
        public uint LogonType;
        public uint Session;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsInfo
    {
        public int State;
        public uint SessionId;
        public uint IncomingBytes;
        public uint OutgoingBytes;
        public uint IncomingFrames;
        public uint OutgoingFrames;
        public uint IncomingCompressedBytes;
        public uint OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long LogonTime;
        public long CurrentTime;
    }

    [DllImport("secur32.dll")]
    private static extern uint LsaGetLogonSessionData(ref Luid logonId, out IntPtr data);

    [DllImport("secur32.dll")]
    private static extern uint LsaFreeReturnBuffer(IntPtr buffer);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
