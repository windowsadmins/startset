using System.Runtime.Versioning;
using Microsoft.Win32;
using StartSet.Core.Constants;

namespace StartSet.Infrastructure.Configuration;

/// <summary>
/// A flat set of named setting values: the policy key, the machine settings key, or a
/// stand-in for either in tests.
/// </summary>
public interface ISettingsStore
{
    /// <summary>The raw value for <paramref name="name"/>, or null when it is not set.</summary>
    object? GetValue(string name);

    /// <summary>Writes one value. Only the machine settings store supports this.</summary>
    void SetValue(string name, object value);
}

/// <summary>
/// A key under HKLM, always read in the 64-bit view: Group Policy, MDM and the MSI all write
/// there, and a stale 32-bit copy under WOW6432Node must never be what a 64-bit run sees.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistrySettingsStore(string subKey) : ISettingsStore
{
    public static RegistrySettingsStore Policy() => new(Paths.PolicyRegistryPath);
    public static RegistrySettingsStore Machine() => new(Paths.MachineSettingsRegistryPath);

    public string SubKey { get; } = subKey;

    public object? GetValue(string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(SubKey);
            return key?.GetValue(name);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes HKLM, so it needs an elevated process; anyone else gets
    /// UnauthorizedAccessException and nothing is written.
    /// </summary>
    public void SetValue(string name, object value)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(SubKey, writable: true);
        switch (value)
        {
            case bool b:
                key.SetValue(name, b ? 1 : 0, RegistryValueKind.DWord);
                break;
            case int i:
                key.SetValue(name, i, RegistryValueKind.DWord);
                break;
            case IEnumerable<string> list:
                key.SetValue(name, list.ToArray(), RegistryValueKind.MultiString);
                break;
            default:
                key.SetValue(name, value.ToString() ?? string.Empty, RegistryValueKind.String);
                break;
        }
    }
}
