using YamlDotNet.Serialization;

namespace StartSet.Core.Models;

/// <summary>
/// StartSet preferences loaded from YAML configuration file.
/// Matches outset's com.github.outset.plist structure.
/// </summary>
public class StartSetPreferences
{
    /// <summary>
    /// Whether to wait for network connectivity before running boot scripts.
    /// Default: true
    /// </summary>
    [YamlMember(Alias = "wait_for_network")]
    public bool WaitForNetwork { get; set; } = true;

    /// <summary>
    /// Timeout in seconds for network connectivity wait.
    /// Default: 180 (3 minutes)
    /// </summary>
    [YamlMember(Alias = "network_timeout")]
    public int NetworkTimeout { get; set; } = 180;

    /// <summary>
    /// Whether to ignore network failure and continue with script execution.
    /// Default: false
    /// </summary>
    [YamlMember(Alias = "ignored_network_failure")]
    public bool IgnoreNetworkFailure { get; set; } = false;

    /// <summary>
    /// Whether to enable verbose logging.
    /// Default: false
    /// </summary>
    [YamlMember(Alias = "verbose")]
    public bool Verbose { get; set; } = false;

    /// <summary>
    /// Whether to enable debug mode (extra detailed logging).
    /// Default: false
    /// </summary>
    [YamlMember(Alias = "debug")]
    public bool Debug { get; set; } = false;

    /// <summary>
    /// Log level override: Debug, Information, Warning, Error
    /// Default: null (determined by verbose/debug flags)
    /// </summary>
    [YamlMember(Alias = "log_level")]
    public string? LogLevel { get; set; }

    /// <summary>
    /// Whether to validate script checksums before execution.
    /// Default: false
    /// </summary>
    [YamlMember(Alias = "checksum_validation")]
    public bool ChecksumValidation { get; set; } = false;

    /// <summary>
    /// List of allowed script extensions (including packages).
    /// Default: [".ps1", ".cmd", ".bat", ".exe", ".msi", ".msix"]
    /// </summary>
    [YamlMember(Alias = "allowed_extensions")]
    public List<string> AllowedExtensions { get; set; } =
    [
        ".ps1",
        ".cmd",
        ".bat",
        ".exe",
        ".msi",
        ".msix"
    ];

    /// <summary>
    /// Maximum script execution timeout in seconds.
    /// Default: 3600 (1 hour)
    ///
    /// This bound is for payloads nobody is waiting on -- boot and privileged
    /// work that may legitimately take a long time. Interactive login payloads
    /// use LoginScriptTimeout instead; see below for why an hour is the wrong
    /// number there.
    /// </summary>
    [YamlMember(Alias = "script_timeout")]
    public int ScriptTimeout { get; set; } = 3600;

    /// <summary>
    /// Maximum execution timeout, in seconds, for payloads that run in a signed-in
    /// user's session -- the login-* and on-demand user contexts.
    /// Default: 120 (two minutes)
    ///
    /// These are bounded far more tightly than ScriptTimeout because someone is
    /// standing in front of the machine and because payloads run one after
    /// another: whatever one payload waits for, every payload behind it waits for
    /// too, and the desktop is unfinished until the batch ends.
    ///
    /// An hour is not a timeout for that work, it is the absence of one. Measured
    /// on a lab workstation on 2026-09-09: a login payload blocked in a
    /// cross-process call to the shell and the entire batch stopped behind it --
    /// no taskbar, no wallpaper, no application window, nothing logged as a
    /// failure. It also blocked shutdown, so the machine could not be restarted
    /// remotely, and it stalled the software-management agent for eighty minutes,
    /// because that agent applies payloads through this engine. Four different
    /// payloads were observed hung the same way that afternoon.
    ///
    /// A payload cannot be trusted not to block: anything that touches the shell,
    /// COM or WinRT can wait indefinitely on a window or a service that is busy,
    /// and at logon the shell is being restarted by the batch itself. Auditing
    /// individual payloads does not fix the class -- bounding them does. Two
    /// minutes is longer than any of these payloads needs and short enough that a
    /// hung one costs a visible pause rather than the session.
    /// </summary>
    [YamlMember(Alias = "login_script_timeout")]
    public int LoginScriptTimeout { get; set; } = 120;

    /// <summary>
    /// Total wall-clock budget, in seconds, for one batch of user-session
    /// payloads. Default: 300 (five minutes).
    ///
    /// LoginScriptTimeout bounds a single payload; this bounds the batch. Both
    /// are needed, because a per-script bound alone still multiplies: twelve
    /// payloads that each burn their two minutes is twenty-four minutes of
    /// unfinished desktop, and the person at the machine cannot tell that from
    /// the hang it replaced.
    ///
    /// When the budget is spent the remaining payloads are not run. They are
    /// recorded as deferred -- explicitly, by name, so what did not happen is
    /// visible rather than merely absent -- and the batch ends so the session is
    /// released. They run again at the next logon.
    ///
    /// Ordering is preserved rather than parallelised on purpose: these payloads
    /// depend on each other, and running them concurrently trades a stall for a
    /// race. Giving up the tail of the batch is the safer failure.
    /// </summary>
    [YamlMember(Alias = "login_batch_budget")]
    public int LoginBatchBudget { get; set; } = 300;

    /// <summary>
    /// Whether to run scripts in parallel within the same payload type.
    /// Default: false (sequential execution)
    /// </summary>
    [YamlMember(Alias = "parallel_execution")]
    public bool ParallelExecution { get; set; } = false;

    /// <summary>
    /// Delay in seconds before running login scripts (allows desktop to settle).
    /// Default: 0
    /// </summary>
    [YamlMember(Alias = "login_delay")]
    public int LoginDelay { get; set; } = 0;

    /// <summary>
    /// Whether to write script output to individual log files.
    /// Default: true
    /// </summary>
    [YamlMember(Alias = "log_script_output")]
    public bool LogScriptOutput { get; set; } = true;

    /// <summary>
    /// List of usernames to ignore for login script execution.
    /// Scripts will not run for these users.
    /// Default: empty
    /// </summary>
    [YamlMember(Alias = "ignored_users")]
    public List<string> IgnoredUsers { get; set; } = [];

    /// <summary>
    /// List of script paths to override (force re-run of run-once scripts).
    /// Scripts in this list will run again even if previously executed.
    /// Default: empty
    /// </summary>
    [YamlMember(Alias = "overrides")]
    public List<string> Overrides { get; set; } = [];

    /// <summary>
    /// Returns default preferences.
    /// </summary>
    public static StartSetPreferences Default => new();
}
