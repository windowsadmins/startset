using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Security;

namespace StartSet.Tests.Helpers;

/// <summary>An in-memory stand-in for the policy or machine settings registry key.</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public InMemorySettingsStore With(string name, object value)
    {
        Values[name] = value;
        return this;
    }

    public object? GetValue(string name) => Values.TryGetValue(name, out var value) ? value : null;

    public void SetValue(string name, object value) => Values[name] = value;
}

/// <summary>Preferences services that never touch the real registry or ACLs.</summary>
public static class TestSettings
{
    /// <summary>Treats every file as trusted: the test wrote it itself.</summary>
    public static TrustResult TrustAll(string path) => TrustResult.Trusted;

    public static PreferencesService Service(
        InMemorySettingsStore? policy = null,
        InMemorySettingsStore? machine = null,
        Func<string, TrustResult>? fileTrust = null) =>
        new(policy ?? new InMemorySettingsStore(), machine ?? new InMemorySettingsStore(), fileTrust ?? TrustAll);
}
