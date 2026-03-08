namespace StartSet.Core.Constants;

/// <summary>
/// System paths and directory constants for StartSet.
/// Matches outset directory structure adapted for Windows.
/// </summary>
public static class Paths
{
    // Installation directory (binary location)
    public const string InstallDirectory = @"C:\Program Files\StartSet";
    public const string ExecutablePath = @"C:\Program Files\StartSet\startset.exe";
    public const string ServiceExecutablePath = @"C:\Program Files\StartSet\StartSetService.exe";

    // Script directories (writable by administrators)
    public const string ScriptRoot = @"C:\ProgramData\ManagedScripts";
    
    // Payload directories (matching outset structure)
    public const string BootOnceDir = @"C:\ProgramData\ManagedScripts\boot-once";
    public const string BootEveryDir = @"C:\ProgramData\ManagedScripts\boot-every";
    public const string LoginWindowDir = @"C:\ProgramData\ManagedScripts\login-window";
    public const string LoginOnceDir = @"C:\ProgramData\ManagedScripts\login-once";
    public const string LoginEveryDir = @"C:\ProgramData\ManagedScripts\login-every";
    public const string LoginPrivilegedOnceDir = @"C:\ProgramData\ManagedScripts\login-privileged-once";
    public const string LoginPrivilegedEveryDir = @"C:\ProgramData\ManagedScripts\login-privileged-every";
    public const string OnDemandDir = @"C:\ProgramData\ManagedScripts\on-demand";
    public const string OnDemandPrivilegedDir = @"C:\ProgramData\ManagedScripts\on-demand-privileged";
    
    // Shared data directory
    public const string ShareDir = @"C:\ProgramData\ManagedScripts\share";
    
    // Configuration file
    public const string PreferencesFile = @"C:\ProgramData\ManagedScripts\Config.yaml";
    
    // Logs directory
    public const string LogDirectory = @"C:\ProgramData\ManagedScripts\logs";
    public const string LogFilePath = @"C:\ProgramData\ManagedScripts\logs\startset.log";
    public const int MaxLogFiles = 30;
    
    // Trigger files (matching outset pattern)
    public const string TriggerOnDemand = @"C:\ProgramData\ManagedScripts\.startset.ondemand";
    public const string TriggerOnDemandPrivileged = @"C:\ProgramData\ManagedScripts\.startset.ondemand-privileged";
    public const string TriggerLoginPrivileged = @"C:\ProgramData\ManagedScripts\.startset.login-privileged";
    public const string TriggerCleanup = @"C:\ProgramData\ManagedScripts\.startset.cleanup";

    /// <summary>
    /// Gets all payload directories that should be created at startup.
    /// </summary>
    public static readonly string[] AllPayloadDirectories =
    [
        BootOnceDir,
        BootEveryDir,
        LoginWindowDir,
        LoginOnceDir,
        LoginEveryDir,
        LoginPrivilegedOnceDir,
        LoginPrivilegedEveryDir,
        OnDemandDir,
        OnDemandPrivilegedDir,
        ShareDir,
        LogDirectory
    ];

    /// <summary>
    /// Gets the run-once tracking file path for a specific user.
    /// </summary>
    /// <param name="username">The username (null for system-wide boot-once tracking)</param>
    /// <returns>Path to the run-once JSON file</returns>
    public static string GetRunOnceFilePath(string? username = null)
    {
        return string.IsNullOrEmpty(username)
            ? Path.Combine(ShareDir, "runonce-system.json")
            : Path.Combine(ShareDir, $"runonce-{username}.json");
    }

    /// <summary>
    /// Gets the checksum file path for validated scripts.
    /// </summary>
    public const string ChecksumFile = @"C:\ProgramData\ManagedScripts\share\checksums.yaml";
}
