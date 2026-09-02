namespace StartSet.Core.Enums;

/// <summary>
/// Script execution result status.
/// </summary>
public enum ExecutionStatus
{
    /// <summary>Script executed successfully (exit code 0).</summary>
    Success,
    
    /// <summary>Script failed with non-zero exit code.</summary>
    Failed,
    
    /// <summary>Script was skipped (run-once already executed).</summary>
    Skipped,
    
    /// <summary>Script file not found.</summary>
    NotFound,
    
    /// <summary>Script checksum validation failed.</summary>
    ChecksumMismatch,
    
    /// <summary>Script execution timed out.</summary>
    Timeout,
    
    /// <summary>Script does not have required permissions.</summary>
    PermissionDenied,
    
    /// <summary>Network wait timed out before script could run.</summary>
    NetworkTimeout,
    
    /// <summary>Script type not supported.</summary>
    UnsupportedType,

    /// <summary>
    /// A user-context payload could not be started in the signed-in user's
    /// session, so it was not run at all.
    ///
    /// Deliberately neither Success nor Failed. The script did not fail -- it
    /// never executed -- but reporting it as a success is what made this class
    /// of problem invisible: a login payload that silently did nothing looked
    /// exactly like one that worked.
    /// </summary>
    Deferred
}
