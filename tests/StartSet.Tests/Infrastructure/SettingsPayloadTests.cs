using FluentAssertions;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Gui;
using StartSet.Tests.Helpers;

namespace StartSet.Tests.Infrastructure;

/// <summary>The Prefs tab's Save: the edits payload and the headless writer.</summary>
public class SettingsPayloadTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static PreferenceSetting Setting(string name) => PreferenceResolver.Find(name)!;

    private string MissingConfig => Path.Combine(_temp.Path, "Config.yaml");

    [Fact]
    public void Payload_RoundTrips_EveryKindOfValue()
    {
        var values = new Dictionary<string, object>
        {
            ["WaitForNetwork"] = false,
            ["NetworkTimeout"] = 60,
            ["LogLevel"] = "Warning",
            ["IgnoredUsers"] = new List<string> { "kiosk", "lab" },
        };

        var back = SettingsPayload.Deserialize(SettingsPayload.Serialize(values));

        back.Should().HaveCount(4);
        back["WaitForNetwork"].Should().Be(false);
        back["NetworkTimeout"].Should().Be(60);
        back["LogLevel"].Should().Be("Warning");
        back["IgnoredUsers"].Should().BeEquivalentTo(new List<string> { "kiosk", "lab" }, o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData("""{ "NoSuchSetting": 1 }""")]
    [InlineData("""{ "NetworkTimeout": -5 }""")]
    [InlineData("""{ "NetworkTimeout": 1.5 }""")]
    [InlineData("""{ "WaitForNetwork": "yes" }""")]
    [InlineData("""{ "LogLevel": "Chatty" }""")]
    [InlineData("""[ 1, 2 ]""")]
    public void Payload_RejectsWhatThePrefsTabWouldNot(string json)
    {
        var read = () => SettingsPayload.Deserialize(json);
        read.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(" warning ", "Warning")]
    [InlineData("", "")]
    public void LogLevel_IsMatchedToAKnownLevel(string input, string expected) =>
        SettingsPayload.Normalize(Setting("LogLevel"), input).Should().Be(expected);

    [Fact]
    public void ListText_IsOneEntryPerLine() =>
        SettingsPayload.Normalize(Setting("IgnoredUsers"), " a \r\n\r\nb\n").Should().BeEquivalentTo(new List<string> { "a", "b" });

    [Fact]
    public void NumberBoxValue_IsAWholeNumber() =>
        SettingsPayload.Normalize(Setting("NetworkTimeout"), 90.0).Should().Be(90);

    [Fact]
    public void Apply_SkipsManagedKeys_AndWritesTheRest()
    {
        var policy = new InMemorySettingsStore().With("NetworkTimeout", 30);
        var machine = new InMemorySettingsStore();
        var service = TestSettings.Service(policy, machine);
        service.Load(MissingConfig);

        var result = SettingsPayload.Apply(new Dictionary<string, object>
        {
            ["NetworkTimeout"] = 999,
            ["Verbose"] = true,
            ["ManifestSigningKey"] = "not from here",
        }, service);

        result.Written.Should().Equal("Verbose");
        result.Skipped.Should().BeEquivalentTo("NetworkTimeout", "ManifestSigningKey");
        machine.Values.Should().ContainKey("Verbose").And.NotContainKey("NetworkTimeout").And.NotContainKey("ManifestSigningKey");
    }

    [Fact]
    public void RunHeadless_WritesThePayloadFile_AndRereadsPolicy()
    {
        var policy = new InMemorySettingsStore().With("Verbose", 0);
        var machine = new InMemorySettingsStore();
        var path = Path.Combine(_temp.Path, "payload.json");
        File.WriteAllText(path, SettingsPayload.Serialize(new Dictionary<string, object>
        {
            ["Verbose"] = true,
            ["ScriptTimeout"] = 600,
        }));

        var code = SettingsPayload.RunHeadless([SettingsPayload.ApplyArgument, path], () => TestSettings.Service(policy, machine));

        code.Should().Be(SettingsPayload.ExitSuccess);
        machine.Values.Should().ContainKey("ScriptTimeout").WhoseValue.Should().Be(600);
        machine.Values.Should().NotContainKey("Verbose", "policy sets it");
    }

    [Fact]
    public void RunHeadless_FailsOnAMissingOrBadPayload()
    {
        var bad = Path.Combine(_temp.Path, "bad.json");
        File.WriteAllText(bad, "not json");

        SettingsPayload.RunHeadless([SettingsPayload.ApplyArgument], () => TestSettings.Service()).Should().Be(SettingsPayload.ExitBadPayload);
        SettingsPayload.RunHeadless([SettingsPayload.ApplyArgument, Path.Combine(_temp.Path, "missing.json")], () => TestSettings.Service())
            .Should().Be(SettingsPayload.ExitBadPayload);
        SettingsPayload.RunHeadless([SettingsPayload.ApplyArgument, bad], () => TestSettings.Service()).Should().Be(SettingsPayload.ExitBadPayload);
    }

    [Fact]
    public void ExitCodes_ReadAsReasons()
    {
        SettingsPayload.ExitCodeText(SettingsPayload.ExitWriteFailed).Should().Be("machine settings could not be written");
        SettingsPayload.ExitCodeText(42).Should().Be("the settings writer exited with code 42");
    }
}
