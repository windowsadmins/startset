namespace StartSet.Core.Enums;

/// <summary>
/// Types of script payloads matching outset's directory structure.
/// </summary>
public enum PayloadType
{
    /// <summary>Scripts run once at boot (admin context), deleted after execution.</summary>
    BootOnce,
    
    /// <summary>Scripts run every boot (admin context).</summary>
    BootEvery,
    
    /// <summary>Scripts run at the login window before user login (admin context).</summary>
    LoginWindow,
    
    /// <summary>Scripts run once per user at login (user context).</summary>
    LoginOnce,
    
    /// <summary>Scripts run every login (user context).</summary>
    LoginEvery,
    
    /// <summary>Scripts run once per user at login with admin privileges.</summary>
    LoginPrivilegedOnce,
    
    /// <summary>Scripts run every login with admin privileges.</summary>
    LoginPrivilegedEvery,
    
    /// <summary>Scripts run on-demand in user context.</summary>
    OnDemand,
    
    /// <summary>Scripts run on-demand with admin privileges.</summary>
    OnDemandPrivileged
}

/// <summary>
/// Extension methods for PayloadType enum.
/// </summary>
public static class PayloadTypeExtensions
{
    /// <summary>
    /// Gets the directory path for this payload type.
    /// </summary>
    public static string GetDirectoryPath(this PayloadType payloadType) => payloadType switch
    {
        PayloadType.BootOnce => Constants.Paths.BootOnceDir,
        PayloadType.BootEvery => Constants.Paths.BootEveryDir,
        PayloadType.LoginWindow => Constants.Paths.LoginWindowDir,
        PayloadType.LoginOnce => Constants.Paths.LoginOnceDir,
        PayloadType.LoginEvery => Constants.Paths.LoginEveryDir,
        PayloadType.LoginPrivilegedOnce => Constants.Paths.LoginPrivilegedOnceDir,
        PayloadType.LoginPrivilegedEvery => Constants.Paths.LoginPrivilegedEveryDir,
        PayloadType.OnDemand => Constants.Paths.OnDemandDir,
        PayloadType.OnDemandPrivileged => Constants.Paths.OnDemandPrivilegedDir,
        _ => throw new ArgumentOutOfRangeException(nameof(payloadType))
    };

    /// <summary>
    /// Returns true if this payload type runs once and should be tracked.
    /// </summary>
    public static bool IsRunOnce(this PayloadType payloadType) => payloadType switch
    {
        PayloadType.BootOnce => true,
        PayloadType.LoginOnce => true,
        PayloadType.LoginPrivilegedOnce => true,
        _ => false
    };

    /// <summary>
    /// Returns true if this payload type requires admin/elevated privileges.
    /// </summary>
    public static bool RequiresElevation(this PayloadType payloadType) => payloadType switch
    {
        PayloadType.BootOnce => true,
        PayloadType.BootEvery => true,
        PayloadType.LoginWindow => true,
        PayloadType.LoginPrivilegedOnce => true,
        PayloadType.LoginPrivilegedEvery => true,
        PayloadType.OnDemandPrivileged => true,
        _ => false
    };

    /// <summary>
    /// Returns true if this payload type runs in user context.
    /// </summary>
    public static bool IsUserContext(this PayloadType payloadType) => payloadType switch
    {
        PayloadType.LoginOnce => true,
        PayloadType.LoginEvery => true,
        PayloadType.OnDemand => true,
        _ => false
    };

    /// <summary>
    /// Returns true if boot-once scripts should be deleted after execution.
    /// </summary>
    public static bool DeleteAfterExecution(this PayloadType payloadType) => payloadType == PayloadType.BootOnce;
}
