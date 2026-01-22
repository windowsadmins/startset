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
    /// </summary>
    [YamlMember(Alias = "script_timeout")]
    public int ScriptTimeout { get; set; } = 3600;

    /// <summary>
    /// Whether to run scripts in parallel within the same payload type.
    /// Default: false (sequential execution)
    /// </summary>
    [YamlMember(Alias = "parallel_execution")]
    public bool ParallelExecution { get; set; } = false;

    /// <summary>
    /// Override directories for payload types (advanced use).
    /// </summary>
    [YamlMember(Alias = "custom_directories")]
    public Dictionary<string, string>? CustomDirectories { get; set; }

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
