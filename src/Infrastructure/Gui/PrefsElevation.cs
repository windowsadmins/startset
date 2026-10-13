using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace StartSet.Infrastructure.Gui;

/// <summary>
/// Decisions behind the Prefs tab's editing. Every setting policy does not set can be edited
/// in the normal, unelevated app; the edits are held until Save. Settings live in HKLM, so a
/// non-elevated app saves by starting its own executable elevated through UAC with
/// <see cref="SettingsPayload.ApplyArgument"/>, which writes them headless and exits.
/// </summary>
public static class PrefsElevation
{
    /// <summary>Win32 ERROR_CANCELLED: the user dismissed the UAC prompt.</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>A setting can be edited unless policy sets it.</summary>
    public static bool CanEdit(bool isPolicyManaged) => !isPolicyManaged;

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

    /// <summary>
    /// Start info that runs <paramref name="exePath"/> elevated to write the settings in
    /// <paramref name="payloadPath"/> to machine settings, with no window.
    /// </summary>
    public static ProcessStartInfo BuildElevatedApply(string exePath, string payloadPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);
        return new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"{SettingsPayload.ApplyArgument} \"{payloadPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
        };
    }
}
