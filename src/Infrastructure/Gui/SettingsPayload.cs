using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using StartSet.Infrastructure.Configuration;

namespace StartSet.Infrastructure.Gui;

/// <summary>What a headless apply wrote and what it left alone.</summary>
public sealed record ApplyResult(IReadOnlyList<string> Written, IReadOnlyList<string> Skipped);

/// <summary>
/// The Prefs tab's unsaved edits, and how they reach machine settings.
/// </summary>
/// <remarks>
/// A non-elevated app cannot write HKLM, so on Save it opens a named pipe with a random name,
/// open only to this user, Administrators and SYSTEM, and starts its own executable elevated
/// with <see cref="ApplyArgument"/> and the pipe's name. The edits go to the elevated run as
/// JSON over the pipe and never touch disk, so a value meant to stay private is not left in a
/// temp file; and a pipe, unlike data protected for the current user, also reaches an
/// administrator who approves UAC under a different account. The elevated run reads policy
/// again, skips every setting policy sets, checks each value with the same rules the Prefs
/// tab uses, writes the rest and exits with no window. An already elevated app calls
/// <see cref="Apply"/> directly.
/// </remarks>
public static class SettingsPayload
{
    /// <summary>Command-line switch that writes a payload file to machine settings and exits.</summary>
    public const string ApplyArgument = "--apply-settings";

    /// <summary>The LogLevel choices on the Prefs tab; empty is "not set".</summary>
    public static readonly string[] LogLevels = ["", "Debug", "Information", "Warning", "Error"];

    public const int ExitSuccess = 0;
    public const int ExitBadPayload = 2;
    public const int ExitWriteFailed = 3;

    /// <summary>The headless writer's exit code in words, for the Prefs tab's status line.</summary>
    public static string ExitCodeText(int code) => code switch
    {
        ExitSuccess => "Saved",
        ExitBadPayload => "the settings to save could not be read",
        ExitWriteFailed => "machine settings could not be written",
        _ => $"the settings writer exited with code {code}"
    };

    // ── Validation ──────────────────────────────────────────────

    /// <summary>
    /// <paramref name="value"/> as the type <paramref name="setting"/> stores, checked with the
    /// Prefs tab's rules: whole numbers of zero or more, one line per list entry, and a known
    /// log level. Throws <see cref="ArgumentException"/> for anything else.
    /// </summary>
    public static object Normalize(PreferenceSetting setting, object? value)
    {
        var type = Nullable.GetUnderlyingType(setting.ValueType) ?? setting.ValueType;

        if (type == typeof(bool))
            return value is bool b ? b : throw Invalid(setting, "must be on or off");

        if (type == typeof(int))
        {
            double? number = value switch
            {
                int i => i,
                long l => l,
                double d => d,
                _ => null
            };
            if (number is not { } n || double.IsNaN(n) || n < 0 || n > int.MaxValue || n != Math.Floor(n))
                throw Invalid(setting, "must be a whole number of zero or more");
            return (int)n;
        }

        if (type == typeof(List<string>))
        {
            var lines = value switch
            {
                string text => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                IEnumerable<string> list => list.Select(s => s?.Trim() ?? string.Empty).Where(s => s.Length > 0),
                null => [],
                _ => throw Invalid(setting, "must be a list of lines")
            };
            return lines.ToList();
        }

        var textValue = value switch
        {
            string s => s,
            null => string.Empty,
            _ => throw Invalid(setting, "must be text")
        };
        if (setting.Name == "LogLevel")
        {
            var level = LogLevels.FirstOrDefault(l => string.Equals(l, textValue.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw Invalid(setting, $"must be one of {string.Join(", ", LogLevels.Where(l => l.Length > 0))}, or empty");
            return level;
        }
        return textValue;
    }

    private static ArgumentException Invalid(PreferenceSetting setting, string rule) =>
        new($"{setting.Name} {rule}");

    // ── Payload file ────────────────────────────────────────────

    /// <summary>The edits as JSON, one property per setting name.</summary>
    public static string Serialize(IReadOnlyDictionary<string, object> values) =>
        JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Reads a payload back, each value checked and typed for its setting. Throws
    /// <see cref="InvalidDataException"/> for a name that is not a setting or a value that
    /// breaks its setting's rules.
    /// </summary>
    public static Dictionary<string, object> Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The settings payload is not a JSON object");

        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var setting = PreferenceResolver.Find(property.Name)
                ?? throw new InvalidDataException($"Unknown setting: {property.Name}");
            try
            {
                values[setting.Name] = Normalize(setting, FromJson(property.Value));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException(ex.Message, ex);
            }
        }
        return values;
    }

    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Array => element.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new InvalidDataException("List entries must be text"))
            .ToList(),
        JsonValueKind.Null => null,
        _ => throw new InvalidDataException($"Unsupported value: {element.ValueKind}")
    };

    // ── Pipe ────────────────────────────────────────────────────

    /// <summary>How long Save waits for the elevated run to connect, which covers the UAC prompt.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A pipe name nobody can guess ahead of time.</summary>
    public static string NewPipeName() => $"startset-settings-{Guid.NewGuid():N}";

    /// <summary>
    /// The pipe's ACL: <paramref name="currentUser"/>, who creates it, and the two accounts the
    /// elevated run can be -- Administrators and SYSTEM. Nobody else may open it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static PipeSecurity PipeSecurityFor(SecurityIdentifier currentUser)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Creates the sending end of the pipe. FirstPipeInstance makes creation fail if anything
    /// already holds the name, so the elevated run can only reach this app.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static NamedPipeServerStream CreatePipe(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.Out, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, inBufferSize: 0, outBufferSize: 0,
            PipeSecurityFor(identity.User!));
    }

    /// <summary>
    /// Waits for the elevated run to connect, then writes the edits and closes the pipe.
    /// Throws <see cref="TimeoutException"/> when nothing connects within <paramref name="timeout"/>.
    /// </summary>
    public static async Task SendAsync(NamedPipeServerStream pipe, IReadOnlyDictionary<string, object> values, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            await pipe.WaitForConnectionAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("the elevated settings writer did not connect");
        }

        var bytes = Encoding.UTF8.GetBytes(Serialize(values));
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
        pipe.WaitForPipeDrain();
        pipe.Disconnect();
    }

    /// <summary>The elevated run's end: connects to <paramref name="pipeName"/> and reads the JSON.</summary>
    public static string ReceiveFromPipe(string pipeName, TimeSpan timeout)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In);
        pipe.Connect((int)timeout.TotalMilliseconds);
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ── Writing ─────────────────────────────────────────────────

    /// <summary>
    /// Writes each value to machine settings through <paramref name="service"/>, skipping any
    /// setting policy sets or that only policy may set. <paramref name="service"/> must have
    /// been loaded so it sees the current policy.
    /// </summary>
    public static ApplyResult Apply(IReadOnlyDictionary<string, object> values, PreferencesService service)
    {
        var written = new List<string>();
        var skipped = new List<string>();
        foreach (var (name, value) in values)
        {
            var setting = PreferenceResolver.Find(name);
            if (setting is null || setting.PolicyOnly || service.IsManaged(setting.Name))
            {
                skipped.Add(name);
                continue;
            }
            service.SaveMachineSetting(setting.Name, Normalize(setting, value));
            written.Add(setting.Name);
        }
        return new ApplyResult(written, skipped);
    }

    /// <summary>
    /// The elevated, windowless half of Save: reads the payload from the pipe named after
    /// <see cref="ApplyArgument"/>, writes it with the real policy and machine settings, and
    /// returns the process exit code. <paramref name="receive"/> and
    /// <paramref name="serviceFactory"/> stand in for the pipe and registry in tests.
    /// </summary>
    public static int RunHeadless(
        IReadOnlyList<string> args,
        Func<PreferencesService>? serviceFactory = null,
        Func<string, string>? receive = null)
    {
        Dictionary<string, object> values;
        try
        {
            var index = args.ToList().FindIndex(a => string.Equals(a, ApplyArgument, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= args.Count)
                return ExitBadPayload;
            var pipeName = args[index + 1];
            values = Deserialize(receive?.Invoke(pipeName) ?? ReceiveFromPipe(pipeName, TimeSpan.FromSeconds(10)));
        }
        catch (Exception)
        {
            return ExitBadPayload;
        }

        try
        {
            var service = serviceFactory?.Invoke() ?? new PreferencesService();
            service.Load();
            Apply(values, service);
            return ExitSuccess;
        }
        catch (Exception)
        {
            return ExitWriteFailed;
        }
    }

    /// <summary>True when the command line asks for a headless apply.</summary>
    public static bool IsApplyRun(IEnumerable<string>? args)
        => args?.Any(a => string.Equals(a, ApplyArgument, StringComparison.OrdinalIgnoreCase)) == true;
}
