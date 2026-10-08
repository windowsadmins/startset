using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using StartSet.Core.Constants;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Gui;
using StartSet.Tests.Helpers;

namespace StartSet.Tests.Infrastructure;

/// <summary>
/// resources/StartSet.admx and its en-US ADML: every setting StartSet reads has a policy,
/// written with the registry type the resolver accepts, in the category matching its Prefs
/// group, and every string and presentation the template names exists.
/// </summary>
public class PolicyTemplateTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";

    private static readonly Lazy<(XDocument Admx, XDocument Adml)> Template = new(() =>
    {
        var root = RepoRoot();
        return (XDocument.Load(Path.Combine(root, "resources", "StartSet.admx")),
                XDocument.Load(Path.Combine(root, "resources", "en-US", "StartSet.adml")));
    });

    private static XDocument Admx => Template.Value.Admx;
    private static XDocument Adml => Template.Value.Adml;

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "StartSet.sln")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not find the repository root above the test output");
    }

    /// <summary>
    /// Values StartSet reads from policy only, which are not settings: they are not on the Prefs
    /// tab and are ignored at every other level. Each is a REG_SZ text element under Security.
    /// </summary>
    public static readonly string[] PolicyOnlyValues = ["ManifestSigningKey"];

    private static IEnumerable<XElement> Policies => Admx.Descendants(Ns + "policy");

    /// <summary>The policy writing <paramref name="valueName"/>, and the element that writes it (null for an on/off policy).</summary>
    private static (XElement Policy, XElement? Element)? PolicyFor(string valueName)
    {
        foreach (var policy in Policies)
        {
            if ((string?)policy.Attribute("valueName") == valueName)
                return (policy, null);
            var element = policy.Element(Ns + "elements")?.Elements()
                .FirstOrDefault(e => (string?)e.Attribute("valueName") == valueName);
            if (element is not null)
                return (policy, element);
        }
        return null;
    }

    public static TheoryData<string> SettingNames()
    {
        var data = new TheoryData<string>();
        foreach (var setting in PreferenceResolver.Settings)
            data.Add(setting.Name);
        return data;
    }

    [Fact]
    public void EveryPolicy_WritesThePolicyKeyTheCodeReads()
    {
        Policies.Should().NotBeEmpty();
        foreach (var policy in Policies)
        {
            ((string?)policy.Attribute("key")).Should().Be(Paths.PolicyRegistryPath, $"{policy.Attribute("name")} must write the key StartSet reads");
            ((string?)policy.Attribute("class")).Should().Be("Machine");
        }
    }

    [Fact]
    public void EveryPolicy_IsASetting()
    {
        var valueNames = Policies.SelectMany(p =>
            new[] { (string?)p.Attribute("valueName") }
                .Concat(p.Element(Ns + "elements")?.Elements().Select(e => (string?)e.Attribute("valueName")) ?? []))
            .Where(v => v is not null);

        foreach (var valueName in valueNames.Where(v => !PolicyOnlyValues.Contains(v)))
            PreferenceResolver.Settings.Should().Contain(s => s.Name == valueName, $"policy value {valueName} must be a setting StartSet reads");
    }

    [Theory]
    [MemberData(nameof(SettingNames))]
    public void EverySetting_HasAPolicyOfTheRightType(string name)
    {
        var setting = PreferenceResolver.Find(name)!;
        var found = PolicyFor(setting.Name);
        found.Should().NotBeNull($"{name} is shown on the Prefs tab, so it must be settable by policy");
        var (policy, element) = found!.Value;

        var type = Nullable.GetUnderlyingType(setting.ValueType) ?? setting.ValueType;
        var expected = type == typeof(bool) ? "onoff"
            : type == typeof(int) ? "decimal"
            : type == typeof(List<string>) ? "multiText"
            : name == "LogLevel" ? "enum"
            : "text";
        var actual = element?.Name.LocalName ?? "onoff";
        actual.Should().Be(expected, $"{name} is a {type.Name}");

        if (expected == "onoff")
        {
            policy.Element(Ns + "enabledValue")!.Element(Ns + "decimal")!.Attribute("value")!.Value.Should().Be("1");
            policy.Element(Ns + "disabledValue")!.Element(Ns + "decimal")!.Attribute("value")!.Value.Should().Be("0");
        }

        policy.Element(Ns + "parentCategory")!.Attribute("ref")!.Value
            .Should().Be(SettingDescriptions.For(name).Group, $"{name}'s category should match its Prefs group");
    }

    [Theory]
    [MemberData(nameof(SettingNames))]
    public void EverySetting_SetTheWayThePolicyWritesIt_IsManagedAndLocked(string name)
    {
        var (_, element) = PolicyFor(name)!.Value;
        object written = element?.Name.LocalName switch
        {
            null => 1,
            "decimal" => int.Parse((string)element.Attribute("minValue")!) + 1,
            "multiText" => new[] { "first", "second" },
            "enum" => element.Descendants(Ns + "string").First().Value,
            _ => "value"
        };

        var svc = TestSettings.Service(policy: new InMemorySettingsStore().With(name, written));
        svc.Load(Path.Combine(Path.GetTempPath(), $"startset-missing-{Guid.NewGuid():N}.yaml"));

        svc.Sources[name].Should().Be(SettingSource.Policy, $"{name} written as the ADMX writes it must be read as policy");
        svc.IsManaged(name).Should().BeTrue();
        PrefsElevation.CanEdit(isElevated: true, isPolicyManaged: svc.Sources[name] == SettingSource.Policy).Should().BeFalse();
    }

    [Fact]
    public void PolicyOnlyValues_AreTextUnderSecurity()
    {
        foreach (var name in PolicyOnlyValues)
        {
            var found = PolicyFor(name);
            found.Should().NotBeNull($"{name} must have a policy");
            var (policy, element) = found!.Value;
            element!.Name.LocalName.Should().Be("text", $"{name} is REG_SZ");
            policy.Element(Ns + "parentCategory")!.Attribute("ref")!.Value.Should().Be("Security");
        }
    }

    [Fact]
    public void EveryCategory_UsedByASetting_Exists()
    {
        var categories = Admx.Descendants(Ns + "category").Select(c => (string)c.Attribute("name")!).ToHashSet();
        foreach (var group in SettingDescriptions.Groups)
            categories.Should().Contain(group);
        foreach (var reference in Admx.Descendants(Ns + "parentCategory").Select(p => (string)p.Attribute("ref")!))
            categories.Should().Contain(reference);
    }

    [Fact]
    public void EveryStringAndPresentation_ResolvesInTheAdml()
    {
        var strings = Adml.Descendants(Ns + "string").Select(s => (string)s.Attribute("id")!).ToList();
        strings.Should().OnlyHaveUniqueItems();
        var presentations = Adml.Descendants(Ns + "presentation").ToDictionary(p => (string)p.Attribute("id")!);

        var text = Admx.ToString();
        foreach (Match m in Regex.Matches(text, @"\$\(string\.([^)]+)\)"))
            strings.Should().Contain(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(text, @"\$\(presentation\.([^)]+)\)"))
            presentations.Should().ContainKey(m.Groups[1].Value);

        foreach (var policy in Policies.Where(p => p.Attribute("presentation") is not null))
        {
            var id = Regex.Match((string)policy.Attribute("presentation")!, @"\$\(presentation\.([^)]+)\)").Groups[1].Value;
            var elementIds = policy.Element(Ns + "elements")!.Elements().Select(e => (string)e.Attribute("id")!).ToHashSet();
            var refIds = presentations[id].Elements().Select(e => (string?)e.Attribute("refId")).Where(r => r is not null).ToList();
            refIds.Should().BeEquivalentTo(elementIds, $"presentation {id} must lay out exactly its policy's elements");
        }
    }
}
