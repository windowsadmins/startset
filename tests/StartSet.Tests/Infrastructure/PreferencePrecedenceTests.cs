using FluentAssertions;
using StartSet.Core.Models;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Security;
using StartSet.Tests.Helpers;

namespace StartSet.Tests.Infrastructure;

/// <summary>
/// The suite precedence, highest first: command line, policy, machine settings, the legacy
/// Config.yaml, the default.
/// </summary>
public class PreferencePrecedenceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Yaml(string content) => _temp.CreateFile("Config.yaml", content);

    [Fact]
    public void Default_WhenNothingIsSet()
    {
        var svc = TestSettings.Service();

        svc.Load(Path.Combine(_temp.Path, "missing.yaml")).NetworkTimeout.Should().Be(180);
        svc.Sources["NetworkTimeout"].Should().Be(SettingSource.Default);
    }

    [Fact]
    public void LegacyFile_BeatsDefault()
    {
        var svc = TestSettings.Service();

        svc.Load(Yaml("network_timeout: 30")).NetworkTimeout.Should().Be(30);
        svc.Sources["NetworkTimeout"].Should().Be(SettingSource.LegacyFile);
    }

    [Fact]
    public void MachineSettings_BeatLegacyFile()
    {
        var svc = TestSettings.Service(machine: new InMemorySettingsStore().With("NetworkTimeout", 60));

        svc.Load(Yaml("network_timeout: 30")).NetworkTimeout.Should().Be(60);
        svc.Sources["NetworkTimeout"].Should().Be(SettingSource.MachineSettings);
    }

    [Fact]
    public void Policy_BeatsMachineSettings()
    {
        var svc = TestSettings.Service(
            policy: new InMemorySettingsStore().With("NetworkTimeout", 90),
            machine: new InMemorySettingsStore().With("NetworkTimeout", 60));

        svc.Load(Yaml("network_timeout: 30")).NetworkTimeout.Should().Be(90);
        svc.Sources["NetworkTimeout"].Should().Be(SettingSource.Policy);
        svc.IsManaged("NetworkTimeout").Should().BeTrue();
    }

    [Fact]
    public void CommandLine_BeatsPolicy()
    {
        var svc = TestSettings.Service(policy: new InMemorySettingsStore().With("Verbose", 0));
        svc.SetCommandLineOverrides(new Dictionary<string, object?> { ["Verbose"] = true });

        svc.Load(Yaml("verbose: false")).Verbose.Should().BeTrue();
        svc.Sources["Verbose"].Should().Be(SettingSource.CommandLine);
    }

    [Fact]
    public void EachLayer_OnlyReplacesWhatItSets()
    {
        var svc = TestSettings.Service(
            policy: new InMemorySettingsStore().With("ScriptTimeout", 100),
            machine: new InMemorySettingsStore().With("LoginDelay", 5));

        var prefs = svc.Load(Yaml("network_timeout: 30"));

        prefs.ScriptTimeout.Should().Be(100);
        prefs.LoginDelay.Should().Be(5);
        prefs.NetworkTimeout.Should().Be(30);
        prefs.WaitForNetwork.Should().BeTrue();
    }

    [Fact]
    public void EnvironmentVariables_AreNotASource()
    {
        Environment.SetEnvironmentVariable("NetworkTimeout", "7");
        try
        {
            var svc = TestSettings.Service(policy: new InMemorySettingsStore().With("NetworkTimeout", 90));
            svc.Load(Path.Combine(_temp.Path, "missing.yaml")).NetworkTimeout.Should().Be(90);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NetworkTimeout", null);
        }
    }

    /// <summary>Every setting the tool reads can be set by policy, and policy wins over the file.</summary>
    [Fact]
    public void EverySetting_IsSettableByPolicy()
    {
        PreferenceResolver.Settings.Should().HaveCount(typeof(StartSetPreferences).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Length);

        foreach (var setting in PreferenceResolver.Settings)
        {
            var (policyValue, expected) = SampleFor(setting.ValueType);
            var svc = TestSettings.Service(policy: new InMemorySettingsStore().With(setting.Name, policyValue));

            var prefs = svc.Load(Path.Combine(_temp.Path, "missing.yaml"));

            svc.Sources[setting.Name].Should().Be(SettingSource.Policy, setting.Name);
            setting.Property.GetValue(prefs).Should().BeEquivalentTo(expected, setting.Name);
        }
    }

    [Fact]
    public void Policy_AcceptsTheConfigYamlNameAsAnAlias()
    {
        var svc = TestSettings.Service(policy: new InMemorySettingsStore().With("network_timeout", 45));

        svc.Load(Path.Combine(_temp.Path, "missing.yaml")).NetworkTimeout.Should().Be(45);
    }

    [Fact]
    public void Lists_FromMultiStringOrSeparatedText()
    {
        var svc = TestSettings.Service(
            policy: new InMemorySettingsStore().With("IgnoredUsers", new[] { "kiosk", "lab" }),
            machine: new InMemorySettingsStore().With("AllowedExtensions", ".ps1; .cmd"));

        var prefs = svc.Load(Path.Combine(_temp.Path, "missing.yaml"));

        prefs.IgnoredUsers.Should().Equal("kiosk", "lab");
        prefs.AllowedExtensions.Should().Equal(".ps1", ".cmd");
    }

    [Fact]
    public void InvalidValue_IsIgnored_AndTheLowerSourceStands()
    {
        var svc = TestSettings.Service(
            policy: new InMemorySettingsStore().With("NetworkTimeout", "soon"),
            machine: new InMemorySettingsStore().With("NetworkTimeout", 60));

        svc.Load(Path.Combine(_temp.Path, "missing.yaml")).NetworkTimeout.Should().Be(60);
        svc.Sources["NetworkTimeout"].Should().Be(SettingSource.MachineSettings);
    }

    [Fact]
    public void UntrustedLegacyFile_IsIgnored()
    {
        var svc = TestSettings.Service(fileTrust: _ => TrustResult.Untrusted("writable by S-1-5-32-545"));

        svc.Load(Yaml("network_timeout: 30")).NetworkTimeout.Should().Be(180);
        svc.Sources["NetworkTimeout"].Should().Be(SettingSource.Default);
    }

    [Fact]
    public void SaveMachineSetting_WritesMachineSettings()
    {
        var machine = new InMemorySettingsStore();
        var svc = TestSettings.Service(machine: machine);
        svc.Load(Path.Combine(_temp.Path, "missing.yaml"));

        svc.SaveMachineSetting("IgnoredUsers", new List<string> { "kiosk" });

        machine.Values["IgnoredUsers"].Should().BeEquivalentTo(new List<string> { "kiosk" });
        svc.Preferences.IgnoredUsers.Should().Equal("kiosk");
    }

    [Fact]
    public void SaveMachineSetting_RefusesAPolicyManagedSetting()
    {
        var machine = new InMemorySettingsStore();
        var svc = TestSettings.Service(
            policy: new InMemorySettingsStore().With("IgnoredUsers", new[] { "kiosk" }),
            machine: machine);
        svc.Load(Path.Combine(_temp.Path, "missing.yaml"));

        var act = () => svc.SaveMachineSetting("IgnoredUsers", new List<string> { "other" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*policy*");
        machine.Values.Should().BeEmpty();
    }

    private static (object PolicyValue, object? Expected) SampleFor(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(bool)) return (1, true);
        if (underlying == typeof(int)) return (4242, 4242);
        if (underlying == typeof(string)) return ("Warning", "Warning");
        if (underlying == typeof(List<string>)) return (new[] { "a", "b" }, new List<string> { "a", "b" });
        throw new InvalidOperationException($"No sample for {type}: add one so the policy test covers it");
    }
}
