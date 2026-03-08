using FluentAssertions;
using StartSet.Infrastructure.Configuration;
using StartSet.Core.Models;
using StartSet.Tests.Helpers;

namespace StartSet.Tests.Infrastructure;

public class PreferencesServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Load_NoFile_ReturnsDefaults()
    {
        var svc = new PreferencesService();
        var prefs = svc.Load(Path.Combine(_temp.Path, "nonexistent.yaml"));

        prefs.Should().NotBeNull();
        prefs.WaitForNetwork.Should().BeTrue();
        prefs.NetworkTimeout.Should().Be(180);
    }

    [Fact]
    public void Load_ValidYaml_DeserializesCorrectly()
    {
        var yaml = """
            wait_for_network: false
            network_timeout: 60
            verbose: true
            script_timeout: 120
            ignored_users:
              - admin
              - serviceaccount
            """;

        var filePath = _temp.CreateFile("config.yaml", yaml);
        var svc = new PreferencesService();
        var prefs = svc.Load(filePath);

        prefs.WaitForNetwork.Should().BeFalse();
        prefs.NetworkTimeout.Should().Be(60);
        prefs.Verbose.Should().BeTrue();
        prefs.ScriptTimeout.Should().Be(120);
        prefs.IgnoredUsers.Should().BeEquivalentTo(["admin", "serviceaccount"]);
    }

    [Fact]
    public void Load_MalformedYaml_ReturnsDefaults()
    {
        var filePath = _temp.CreateFile("bad.yaml", "{{{{not yaml at all!!!}}}}");
        var svc = new PreferencesService();
        var prefs = svc.Load(filePath);

        // Should gracefully fall back to defaults
        prefs.Should().NotBeNull();
        prefs.WaitForNetwork.Should().BeTrue();
    }

    [Fact]
    public void Load_PartialYaml_MergesWithDefaults()
    {
        // Only override one property — rest should be defaults
        var yaml = "network_timeout: 30";

        var filePath = _temp.CreateFile("partial.yaml", yaml);
        var svc = new PreferencesService();
        var prefs = svc.Load(filePath);

        prefs.NetworkTimeout.Should().Be(30);
        prefs.WaitForNetwork.Should().BeTrue(); // default
        prefs.ScriptTimeout.Should().Be(3600); // default
    }

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        var filePath = Path.Combine(_temp.Path, "roundtrip.yaml");
        var svc = new PreferencesService();

        var original = new StartSetPreferences
        {
            WaitForNetwork = false,
            NetworkTimeout = 45,
            Verbose = true,
            ScriptTimeout = 999,
            IgnoredUsers = ["testuser"]
        };

        svc.Save(original, filePath);
        var loaded = svc.Load(filePath);

        loaded.WaitForNetwork.Should().Be(false);
        loaded.NetworkTimeout.Should().Be(45);
        loaded.Verbose.Should().Be(true);
        loaded.ScriptTimeout.Should().Be(999);
        loaded.IgnoredUsers.Should().Contain("testuser");
    }

    [Fact]
    public void Save_CreatesDirectoryIfMissing()
    {
        var filePath = Path.Combine(_temp.Path, "nested", "dir", "config.yaml");
        var svc = new PreferencesService();

        svc.Save(StartSetPreferences.Default, filePath);

        File.Exists(filePath).Should().BeTrue();
    }

    [Fact]
    public void EnsureDefaultPreferences_CreatesFileWhenMissing()
    {
        var filePath = Path.Combine(_temp.Path, "defaults.yaml");
        var svc = new PreferencesService();
        svc.Load(filePath); // sets the internal path

        svc.EnsureDefaultPreferences();

        File.Exists(filePath).Should().BeTrue();
    }

    [Fact]
    public void Preferences_Property_ReflectsLastLoad()
    {
        var yaml = "verbose: true";
        var filePath = _temp.CreateFile("live.yaml", yaml);
        var svc = new PreferencesService();

        svc.Load(filePath);
        svc.Preferences.Verbose.Should().BeTrue();
    }

    [Fact]
    public void Load_UnknownProperties_AreIgnored()
    {
        var yaml = """
            wait_for_network: true
            some_future_property: 42
            another_unknown: "hello"
            """;

        var filePath = _temp.CreateFile("future.yaml", yaml);
        var svc = new PreferencesService();

        // Should not throw on unknown properties
        var prefs = svc.Load(filePath);
        prefs.Should().NotBeNull();
    }
}
