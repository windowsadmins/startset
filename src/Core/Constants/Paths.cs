namespace StartSet.Core.Constants;

/// <summary>
/// System paths and directory constants for StartSet.
/// Matches outset directory structure adapted for Windows.
/// </summary>
public static class Paths
{
    // Installation directory (binary location)
    public const string InstallDirectory = @"C:\Program Files\StartSet";
    public const string ExecutablePath = @"C:\Program Files\StartSet\managedstatekeeper.exe";
    public const string ServiceExecutablePath = @"C:\Program Files\StartSet\StartSetService.exe";

    // Script directories (writable by administrators)
    public const string ScriptRoot = @"C:\ProgramData\ManagedState";
    
    // Payload directories (matching outset structure)
    public const string BootOnceDir = @"C:\ProgramData\ManagedState\boot-once";
    public const string BootEveryDir = @"C:\ProgramData\ManagedState\boot-every";
    public const string LoginWindowDir = @"C:\ProgramData\ManagedState\login-window";
    public const string LoginOnceDir = @"C:\ProgramData\ManagedState\login-once";
    public const string LoginEveryDir = @"C:\ProgramData\ManagedState\login-every";
    public const string LoginPrivilegedOnceDir = @"C:\ProgramData\ManagedState\login-privileged-once";
    public const string LoginPrivilegedEveryDir = @"C:\ProgramData\ManagedState\login-privileged-every";
    public const string OnDemandDir = @"C:\ProgramData\ManagedState\on-demand";
    public const string OnDemandPrivilegedDir = @"C:\ProgramData\ManagedState\on-demand-privileged";
    
    // Shared data directory
    public const string ShareDir = @"C:\ProgramData\ManagedState\share";
    
    // Configuration file
    public const string PreferencesFile = @"C:\ProgramData\ManagedState\Config.yaml";
    
    // Logs directory (day-nested session structure: logs/YYYY-MM-DD/HHMM/)
    public const string LogDirectory = @"C:\ProgramData\ManagedState\logs";
    public const string ReportsDirectory = @"C:\ProgramData\ManagedState\reports";

    // Verbose installer logs from package payloads. Namespaced so the log root holds
    // only the dated session tree -- these are far too large to fold into a session log.
    public const string InstallLogDirectory = @"C:\ProgramData\ManagedState\logs\installs";
    public const int MaxRetentionDays = 30;
    
    // Settings. Precedence, highest first: a one-off command-line flag, policy, machine
    // settings, the legacy Config.yaml, the built-in default. Both keys are read in the
    // 64-bit registry view.
    public const string PolicyRegistryPath = @"SOFTWARE\Policies\StartSet";
    public const string MachineSettingsRegistryPath = @"SOFTWARE\StartSet\Settings";

    // The one folder under ScriptRoot a standard user may create files in. Everything
    // else under ScriptRoot is writable only by Administrators and SYSTEM, so a user who
    // needs an on-demand run drops a trigger file here instead of being given write access.
    public const string TriggerDirectory = @"C:\ProgramData\ManagedState\triggers";

    // Trigger files (matching outset pattern). Honoured here and in TriggerDirectory.
    public const string TriggerOnDemand = @"C:\ProgramData\ManagedState\.startset.ondemand";
    public const string TriggerOnDemandPrivileged = @"C:\ProgramData\ManagedState\.startset.ondemand-privileged";
    public const string TriggerLogin = @"C:\ProgramData\ManagedState\.startset.login";
    public const string TriggerLoginPrivileged = @"C:\ProgramData\ManagedState\.startset.login-privileged";
    public const string TriggerCleanup = @"C:\ProgramData\ManagedState\.startset.cleanup";

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
        TriggerDirectory,
        LogDirectory,
        ReportsDirectory
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
    public const string ChecksumFile = @"C:\ProgramData\ManagedState\share\checksums.yaml";
}
