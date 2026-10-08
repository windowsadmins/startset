namespace StartSet.Infrastructure.Configuration;

/// <summary>How the GUI presents one setting.</summary>
public sealed record SettingDescription(string Group, string Label, string Help, string? Unit = null);

/// <summary>
/// Labels and grouping for the GUI's Prefs tab. Every setting in
/// <see cref="PreferenceResolver.Settings"/> has an entry, which a test enforces, so the tab
/// never silently leaves one out.
/// </summary>
public static class SettingDescriptions
{
    public const string Network = "Network";
    public const string Execution = "Execution";
    public const string Login = "Login";
    public const string Logging = "Logging";

    public static IReadOnlyList<string> Groups { get; } = [Network, Execution, Login, Logging];

    private static readonly Dictionary<string, SettingDescription> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WaitForNetwork"] = new(Network, "Wait for network before boot payloads", "Holds boot payloads until the network is up."),
        ["NetworkTimeout"] = new(Network, "Network wait", "How long to wait for the network.", "seconds"),
        ["IgnoreNetworkFailure"] = new(Network, "Run payloads without network", "Runs boot payloads even when the network never came up."),

        ["ScriptTimeout"] = new(Execution, "Payload timeout", "Longest a boot or privileged payload may run.", "seconds"),
        ["AllowedExtensions"] = new(Execution, "Allowed file types", "Extensions StartSet runs. One per line."),
        ["ChecksumValidation"] = new(Execution, "Require checksums", "Runs only payloads whose checksum is recorded."),
        ["ParallelExecution"] = new(Execution, "Parallel execution", "Not implemented yet: payloads still run one at a time."),
        ["Overrides"] = new(Execution, "Run-once overrides", "Run-once payloads to run again. One file name per line."),
        ["ManifestSigningKey"] = new(Execution, "Script signing key", "Ed25519 public key. When set, only scripts signed with the matching private key run. Set by policy only."),

        ["LoginScriptTimeout"] = new(Login, "Login payload timeout", "Longest one payload in a user's session may run.", "seconds"),
        ["LoginBatchBudget"] = new(Login, "Login batch budget", "Total time for one batch of login payloads.", "seconds"),
        ["LoginDelay"] = new(Login, "Login delay", "Pause before login payloads run.", "seconds"),
        ["ShellReadyTimeout"] = new(Login, "Desktop wait", "How long to wait for the user's desktop before login payloads.", "seconds"),
        ["ShellSettleDelay"] = new(Login, "Desktop settle", "Pause after the desktop appears.", "seconds"),
        ["LogonCatchUpGrace"] = new(Login, "Missed logon grace", "Wait after start-up before running login payloads for a user already signed in.", "seconds"),
        ["IgnoredUsers"] = new(Login, "Ignored users", "Accounts login payloads never run for. One per line."),

        ["Verbose"] = new(Logging, "Verbose logging", "Writes debug detail to the log."),
        ["Debug"] = new(Logging, "Debug logging", "Writes debug detail to the log."),
        ["LogLevel"] = new(Logging, "Log level", "Debug, Information, Warning or Error. Overrides the two switches above."),
        ["LogScriptOutput"] = new(Logging, "Log payload output", "Folds each payload's output into the session log."),
    };

    /// <summary>The description for <paramref name="name"/>, or a plain one built from the name.</summary>
    public static SettingDescription For(string name) =>
        Descriptions.TryGetValue(name, out var description)
            ? description
            : new SettingDescription(Execution, name, string.Empty);

    /// <summary>True when <paramref name="name"/> has a written description.</summary>
    public static bool Has(string name) => Descriptions.ContainsKey(name);
}
