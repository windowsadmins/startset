using System.Linq;
using FluentAssertions;
using StartSet.Engine;
using Xunit;

namespace StartSet.Tests.Engine;

/// <summary>
/// Payload output folded into the session log.
/// </summary>
/// <remarks>
/// The regression these guard: output used to be written to a sidecar file named after
/// the script. When no session was attached that file landed loose in the log root,
/// where nothing downstream reads it and only age could remove it.
/// </remarks>
public class ScriptOutputFormattingTests
{
    [Fact]
    public void PrefixesEachLineWithScriptAndStream()
    {
        var lines = ExecutionEngine.FormatOutputLines("SamplePayload", "stdout", "first\nsecond");

        lines.Should().Equal(
            "[SamplePayload] stdout | first",
            "[SamplePayload] stdout | second");
    }

    [Fact]
    public void HandlesWindowsLineEndings()
    {
        var lines = ExecutionEngine.FormatOutputLines("AnotherPayload", "stdout", "one\r\ntwo");

        lines.Should().Equal(
            "[AnotherPayload] stdout | one",
            "[AnotherPayload] stdout | two");
    }

    [Fact]
    public void SkipsBlankLines()
    {
        var lines = ExecutionEngine.FormatOutputLines("Script", "stdout", "a\n\n   \nb");

        lines.Should().Equal(
            "[Script] stdout | a",
            "[Script] stdout | b");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmitsNothingForEmptyOutput(string? content)
    {
        ExecutionEngine.FormatOutputLines("Script", "stdout", content).Should().BeEmpty();
    }

    [Fact]
    public void CapsRunawayOutputAndSaysSo()
    {
        var content = string.Join("\n", Enumerable.Range(0, 5000).Select(i => $"line {i}"));

        var lines = ExecutionEngine.FormatOutputLines("Chatty", "stdout", content);

        // 500 lines of output plus the notice that says the rest was dropped.
        lines.Should().HaveCount(501);
        lines.Last().Should().Be("[Chatty] stdout | ... output truncated after 500 lines");
    }

    [Fact]
    public void LabelsTheStreamItCameFrom()
    {
        var lines = ExecutionEngine.FormatOutputLines("Script", "stderr", "it failed");

        lines.Should().ContainSingle().Which.Should().Be("[Script] stderr | it failed");
    }
}
