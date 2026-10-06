using System.Runtime.Versioning;
using StartSet.Core.Constants;

namespace StartSet.Infrastructure.Security;

/// <summary>
/// Which trigger files the service acts on, and from whom.
/// </summary>
/// <remarks>
/// A trigger file is read for its name only; its content is never used. It lives in
/// either ScriptRoot, which only administrators can write, or TriggerDirectory, where any
/// user may create files. That is how a standard user asks for an on-demand run without
/// write access to anything StartSet executes.
///
/// The user-context triggers (.startset.ondemand, .startset.login) run payloads in the
/// signed-in user's own session, so anyone may ask for them. The privileged ones run
/// payloads as SYSTEM and are honoured only from a file an administrator owns; one a
/// standard user created is deleted and logged.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class TriggerFiles
{
    /// <summary>Folders that are watched for trigger files.</summary>
    public static readonly string[] Directories = [Paths.ScriptRoot, Paths.TriggerDirectory];

    private static readonly HashSet<string> UserTriggers = new(StringComparer.OrdinalIgnoreCase)
    {
        Path.GetFileName(Paths.TriggerOnDemand),
        Path.GetFileName(Paths.TriggerLogin)
    };

    /// <summary>Every place the trigger named like <paramref name="triggerPath"/> may be.</summary>
    public static string[] LocationsOf(string triggerPath)
    {
        var name = Path.GetFileName(triggerPath);
        return Directories.Select(d => Path.Combine(d, name)).ToArray();
    }

    /// <summary>True when the trigger runs payloads as SYSTEM, or changes what the service does.</summary>
    public static bool IsPrivileged(string triggerPath) => !UserTriggers.Contains(Path.GetFileName(triggerPath));

    /// <summary>
    /// Whether the trigger at <paramref name="path"/> may be acted on, given its owner.
    /// </summary>
    public static TrustResult Evaluate(string path, string? ownerSid)
    {
        if (!IsPrivileged(path) || FileTrust.IsAdministrativeSid(ownerSid))
            return TrustResult.Trusted;
        return TrustResult.Untrusted(
            $"{Path.GetFileName(path)} runs payloads as SYSTEM and was created by {ownerSid ?? "an unknown owner"}, not an administrator");
    }

    /// <summary>
    /// Deletes every copy of the trigger and reports whether any was one the service may act
    /// on. Rejected copies are reported through <paramref name="rejected"/>.
    /// </summary>
    public static bool Consume(string triggerPath, Action<string>? rejected = null, Func<string, string?>? ownerOf = null)
    {
        ownerOf ??= FileTrust.OwnerOf;
        var accepted = false;
        foreach (var location in LocationsOf(triggerPath))
        {
            if (!File.Exists(location)) continue;
            var decision = Evaluate(location, ownerOf(location));
            if (decision.IsTrusted) accepted = true;
            else rejected?.Invoke(decision.Reason!);
            try { File.Delete(location); } catch { }
        }
        return accepted;
    }
}
