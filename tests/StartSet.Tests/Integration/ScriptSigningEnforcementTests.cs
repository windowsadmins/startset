using System.Text;
using FluentAssertions;
using StartSet.Core.Enums;
using StartSet.Engine;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Security;
using StartSet.Tests.Helpers;

namespace StartSet.Tests.Integration;

/// <summary>
/// Required script signing end to end: policy sets the key, discovery verifies each payload,
/// and a payload that fails is refused and reported rather than run or quietly skipped.
/// </summary>
public class ScriptSigningEnforcementTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly (string Public, string Private) _keys = ScriptSigning.GenerateKeypair();

    public void Dispose() => _temp.Dispose();

    private PreferencesService Service(InMemorySettingsStore? policy = null, InMemorySettingsStore? machine = null, string yaml = "")
    {
        var service = TestSettings.Service(policy: policy, machine: machine);
        service.Load(_temp.CreateFile("Config.yaml", yaml));
        return service;
    }

    private PreferencesService Enforced(string? key = null) =>
        Service(new InMemorySettingsStore().With("ManifestSigningKey", key ?? _keys.Public));

    private void Payload(string name, string content) =>
        _temp.CreateFile(Path.Combine("boot-every", name), content);

    private void SignedPayload(string name, string content)
    {
        var path = Path.Combine("boot-every", name);
        var signed = ScriptSigning.Sign(Encoding.UTF8.GetBytes(content), name, ScriptSigning.ParsePrivateKey(_keys.Private)!);
        _temp.CreateFile(path, Encoding.UTF8.GetString(signed));
    }

    private Task<List<StartSet.Core.Models.ExecutionResult>> Run(PreferencesService service) =>
        new ExecutionEngine(service, _temp.Path, TestSettings.TrustAll).ExecuteAsync([PayloadType.BootEvery], waitForNetwork: false);

    [Fact]
    public async Task Enforced_UnsignedScript_IsRefusedAndReported()
    {
        Payload("Unsigned.ps1", "exit 0");

        var results = await Run(Enforced());

        results.Should().ContainSingle();
        results[0].Status.Should().Be(ExecutionStatus.SignatureRejected);
        results[0].ErrorMessage.Should().Contain("no signature");
    }

    [Fact]
    public async Task Enforced_SignedScript_IsNotRefused()
    {
        // No assertion that it succeeded: boot-every needs elevation, which the test
        // runner may not have. The claim is only that signing let it through.
        SignedPayload("Signed.ps1", "exit 0\n");

        var results = await Run(Enforced());

        results.Should().ContainSingle();
        results[0].Status.Should().NotBe(ExecutionStatus.SignatureRejected);
    }

    [Fact]
    public async Task Enforced_TamperedScript_IsRefused()
    {
        var signed = ScriptSigning.Sign("exit 0\n"u8.ToArray(), "Tampered.ps1", ScriptSigning.ParsePrivateKey(_keys.Private)!);
        Payload("Tampered.ps1", Encoding.UTF8.GetString(signed).Replace("exit 0", "exit 1"));

        var results = await Run(Enforced());

        results.Single().Status.Should().Be(ExecutionStatus.SignatureRejected);
        results.Single().ErrorMessage.Should().Contain("bad signature");
    }

    [Fact]
    public async Task Enforced_Executable_IsRefused()
    {
        Payload("Tool.exe", "MZ");

        var results = await Run(Enforced());

        results.Single().Status.Should().Be(ExecutionStatus.SignatureRejected);
        results.Single().ErrorMessage.Should().Contain("cannot carry");
    }

    [Fact]
    public async Task Enforced_InvalidPolicyKey_RefusesEverything()
    {
        SignedPayload("Signed.ps1", "exit 0\n");

        var results = await Run(Enforced("not-a-key"));

        results.Single().Status.Should().Be(ExecutionStatus.SignatureRejected);
        results.Single().ErrorMessage.Should().Contain("bad key");
    }

    [Fact]
    public async Task NotEnforced_UnsignedScript_RunsAsBefore()
    {
        Payload("Unsigned.ps1", "exit 0");

        var results = await Run(Service());

        results.Single().Status.Should().NotBe(ExecutionStatus.SignatureRejected);
    }

    [Fact]
    public async Task Enforced_NestedFolder_StillNotDiscovered()
    {
        SignedPayload("Signed.ps1", "exit 0\n");
        _temp.CreateFile(Path.Combine("boot-every", "nested", "Inner.ps1"), "exit 0");

        var results = await Run(Enforced());

        results.Select(r => r.Script.FileName).Should().Equal("Signed.ps1");
    }

    [Fact]
    public void KeyFromMachineSettings_IsIgnored()
    {
        var service = Service(machine: new InMemorySettingsStore().With("ManifestSigningKey", _keys.Public));

        service.Preferences.ManifestSigningKey.Should().BeNull();
        service.Sources["ManifestSigningKey"].Should().Be(SettingSource.Default);
    }

    [Fact]
    public void KeyFromConfigYaml_IsIgnored()
    {
        var service = Service(yaml: $"manifest_signing_key: {_keys.Public}");

        service.Preferences.ManifestSigningKey.Should().BeNull();
    }

    [Fact]
    public void KeyFromCommandLine_IsIgnored()
    {
        var service = TestSettings.Service();
        service.SetCommandLineOverrides(new Dictionary<string, object?> { ["ManifestSigningKey"] = _keys.Public });
        service.Load(_temp.CreateFile("Config.yaml", ""));

        service.Preferences.ManifestSigningKey.Should().BeNull();
    }

    [Fact]
    public void KeyFromPolicy_IsUsedAndManaged()
    {
        var service = Enforced();

        service.Preferences.ManifestSigningKey.Should().Be(_keys.Public);
        service.IsManaged("ManifestSigningKey").Should().BeTrue();
    }

    [Fact]
    public void KeyCannotBeSavedToMachineSettings()
    {
        var service = Service();

        var save = () => service.SaveMachineSetting("ManifestSigningKey", _keys.Public);

        save.Should().Throw<InvalidOperationException>().WithMessage("*only be set by policy*");
    }
}
