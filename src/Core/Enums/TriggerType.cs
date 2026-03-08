namespace StartSet.Core.Enums;

/// <summary>
/// Types of triggers that can initiate script execution.
/// </summary>
public enum TriggerType
{
    /// <summary>System boot trigger (LaunchDaemon equivalent).</summary>
    Boot,
    
    /// <summary>User login trigger.</summary>
    Login,
    
    /// <summary>Login window trigger (before user authentication).</summary>
    LoginWindow,
    
    /// <summary>Privileged login trigger (after user auth, elevated).</summary>
    LoginPrivileged,
    
    /// <summary>On-demand trigger via trigger file.</summary>
    OnDemand,
    
    /// <summary>Privileged on-demand trigger.</summary>
    OnDemandPrivileged,
    
    /// <summary>Cleanup trigger for on-demand directory.</summary>
    Cleanup,
    
    /// <summary>Manual CLI invocation.</summary>
    Manual
}

/// <summary>
/// Extension methods for TriggerType enum.
/// </summary>
public static class TriggerTypeExtensions
{
    /// <summary>
    /// Gets the trigger file path for this trigger type, if applicable.
    /// </summary>
    public static string? GetTriggerFilePath(this TriggerType triggerType) => triggerType switch
    {
        TriggerType.OnDemand => Constants.Paths.TriggerOnDemand,
        TriggerType.OnDemandPrivileged => Constants.Paths.TriggerOnDemandPrivileged,
        TriggerType.LoginPrivileged => Constants.Paths.TriggerLoginPrivileged,
        TriggerType.Cleanup => Constants.Paths.TriggerCleanup,
        _ => null
    };

    /// <summary>
    /// Gets the payload types associated with this trigger.
    /// </summary>
    public static PayloadType[] GetPayloadTypes(this TriggerType triggerType) => triggerType switch
    {
        TriggerType.Boot => [PayloadType.BootOnce, PayloadType.BootEvery],
        TriggerType.Login => [PayloadType.LoginOnce, PayloadType.LoginEvery],
        TriggerType.LoginWindow => [PayloadType.LoginWindow],
        TriggerType.LoginPrivileged => [PayloadType.LoginPrivilegedOnce, PayloadType.LoginPrivilegedEvery],
        TriggerType.OnDemand => [PayloadType.OnDemand],
        TriggerType.OnDemandPrivileged => [PayloadType.OnDemandPrivileged],
        TriggerType.Cleanup => [], // Cleanup doesn't run payloads
        TriggerType.Manual => [], // Manual can be any - handled separately
        _ => []
    };
}
