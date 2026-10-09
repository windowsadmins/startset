namespace StartSet.Core.Models;

/// <summary>
/// Marks a setting that only policy (HKLM\SOFTWARE\Policies\StartSet) may set. A value for it
/// in machine settings, Config.yaml or on the command line is ignored and logged.
/// </summary>
/// <remarks>
/// For settings that exist to constrain what StartSet runs. If an administrator on the
/// machine -- or anything running as one -- could set them in a writable place, they would
/// constrain nothing.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PolicyOnlyAttribute : Attribute
{
}
