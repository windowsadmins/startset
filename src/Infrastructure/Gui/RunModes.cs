using StartSet.Core.Constants;

namespace StartSet.Infrastructure.Gui;

/// <summary>The runs the GUI's Run tab offers.</summary>
public enum RunMode
{
    /// <summary>on-demand payloads, in the signed-in user's session.</summary>
    OnDemand,

    /// <summary>on-demand-privileged payloads, elevated.</summary>
    OnDemandPrivileged,

    /// <summary>login-privileged-once and login-privileged-every payloads, elevated.</summary>
    LoginPrivileged
}

/// <summary>
/// How each run is started and which session log it writes.
/// </summary>
/// <remarks>
/// An on-demand run belongs in the signed-in user's session, and only SYSTEM can start a
/// process there, so the GUI asks the service for it by dropping a trigger file in the
/// triggers folder -- the one place a standard user may write -- and follows the service's
/// "trigger" session log. The privileged runs need an elevated process: the CLI is started
/// elevated and its own session log is followed.
/// </remarks>
public static class RunModes
{
    public static IReadOnlyList<RunMode> All { get; } = [RunMode.OnDemand, RunMode.OnDemandPrivileged, RunMode.LoginPrivileged];

    public static string Title(RunMode mode) => mode switch
    {
        RunMode.OnDemand => "On demand",
        RunMode.OnDemandPrivileged => "On demand, privileged",
        RunMode.LoginPrivileged => "Login, privileged",
        _ => mode.ToString()
    };

    public static string Description(RunMode mode) => mode switch
    {
        RunMode.OnDemand => "Runs the on-demand payloads in your session. The StartSet service runs them; no administrator rights needed.",
        RunMode.OnDemandPrivileged => "Runs the on-demand-privileged payloads as administrator. Asks for elevation.",
        RunMode.LoginPrivileged => "Runs the login-privileged payloads as administrator, without signing out. Asks for elevation.",
        _ => string.Empty
    };

    /// <summary>True when the run is handed to the service through a trigger file.</summary>
    public static bool UsesTrigger(RunMode mode) => mode == RunMode.OnDemand;

    /// <summary>The trigger file a run drops, for the runs that use one.</summary>
    public static string? TriggerPath(RunMode mode) => mode switch
    {
        RunMode.OnDemand => Path.Combine(Paths.TriggerDirectory, Path.GetFileName(Paths.TriggerOnDemand)),
        _ => null
    };

    /// <summary>The CLI arguments for the runs started as an elevated CLI process.</summary>
    public static IReadOnlyList<string> CliArguments(RunMode mode) => mode switch
    {
        RunMode.OnDemandPrivileged => ["on-demand", "--privileged", "--verbose"],
        RunMode.LoginPrivileged => ["login-privileged", "--verbose"],
        _ => []
    };

    /// <summary>
    /// The run type in the session folder name (logs\YYYY-MM-DD\HHMM-runtype). The CLI names a
    /// session after its first argument; the service names a trigger run "trigger".
    /// </summary>
    public static string SessionRunType(RunMode mode) => mode switch
    {
        RunMode.OnDemand => "trigger",
        RunMode.OnDemandPrivileged => "on-demand",
        RunMode.LoginPrivileged => "login-privileged",
        _ => mode.ToString()
    };
}
