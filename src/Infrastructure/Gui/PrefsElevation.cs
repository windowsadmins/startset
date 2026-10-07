using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace StartSet.Infrastructure.Gui;

/// <summary>
/// Decisions behind the GUI's read-only Prefs tab. Settings live in HKLM, so they can only be
/// changed by an elevated process: a non-elevated GUI shows them read-only and "Unlock"
/// relaunches the app itself through UAC, opening straight on the Prefs tab. There is no
/// SYSTEM service and no user-writable file in the path.
/// </summary>
public static class PrefsElevation
{
    /// <summary>Command-line switch that opens the GUI on the Prefs tab.</summary>
    public const string PrefsArgument = "--prefs";

    /// <summary>Win32 ERROR_CANCELLED: the user dismissed the UAC prompt.</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>True when the command line asks the GUI to open on the Prefs tab.</summary>
    public static bool OpensOnPrefs(IEnumerable<string>? args)
        => args?.Any(a => string.Equals(a?.Trim(), PrefsArgument, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// A setting can be edited only by an elevated process, and never when policy manages it.
    /// </summary>
    public static bool CanEdit(bool isElevated, bool isPolicyManaged) => isElevated && !isPolicyManaged;

    /// <summary>True when the exception is the user cancelling the UAC prompt, not a real failure.</summary>
    public static bool IsElevationCancelled(Exception? ex)
        => ex is Win32Exception { NativeErrorCode: ErrorCancelled };

    /// <summary>
    /// Whether this process holds an elevated administrator token. With UAC on, an admin's
    /// filtered token is not in the Administrators role, so this is false until elevated.
    /// </summary>
    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Start info that relaunches <paramref name="exePath"/> elevated on the Prefs tab.</summary>
    public static ProcessStartInfo BuildElevatedRelaunch(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        return new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = PrefsArgument,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
        };
    }
}
