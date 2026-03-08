using FluentAssertions;
using StartSet.Core.Enums;
using StartSet.Core.Models;
using StartSet.Engine.Processors;

namespace StartSet.Tests.Engine;

public class ProcessorRoutingTests
{
    private static ScriptPayload Script(string ext) => new()
    {
        FilePath = $@"C:\scripts\test{ext}",
        PayloadType = PayloadType.BootEvery
    };

    // ──────────────── PowerShellProcessor ────────────────

    [Fact]
    public void PowerShell_CanProcess_Ps1()
    {
        new PowerShellProcessor().CanProcess(Script(".ps1")).Should().BeTrue();
    }

    [Fact]
    public void PowerShell_CannotProcess_Other()
    {
        var ps = new PowerShellProcessor();
        ps.CanProcess(Script(".cmd")).Should().BeFalse();
        ps.CanProcess(Script(".bat")).Should().BeFalse();
        ps.CanProcess(Script(".exe")).Should().BeFalse();
    }

    [Fact]
    public void PowerShell_SupportedExtensions()
    {
        new PowerShellProcessor().SupportedExtensions.Should().Contain(".ps1");
    }

    // ──────────────── BatchProcessor ────────────────

    [Fact]
    public void Batch_CanProcess_CmdAndBat()
    {
        var bp = new BatchProcessor();
        bp.CanProcess(Script(".cmd")).Should().BeTrue();
        bp.CanProcess(Script(".bat")).Should().BeTrue();
    }

    [Fact]
    public void Batch_CannotProcess_Other()
    {
        var bp = new BatchProcessor();
        bp.CanProcess(Script(".ps1")).Should().BeFalse();
        bp.CanProcess(Script(".exe")).Should().BeFalse();
    }

    [Fact]
    public void Batch_SupportedExtensions()
    {
        var exts = new BatchProcessor().SupportedExtensions;
        exts.Should().Contain(".cmd");
        exts.Should().Contain(".bat");
    }

    // ──────────────── ExecutableProcessor ────────────────

    [Fact]
    public void Executable_CanProcess_Exe()
    {
        new ExecutableProcessor().CanProcess(Script(".exe")).Should().BeTrue();
    }

    [Fact]
    public void Executable_CannotProcess_Other()
    {
        var ep = new ExecutableProcessor();
        ep.CanProcess(Script(".ps1")).Should().BeFalse();
        ep.CanProcess(Script(".msi")).Should().BeFalse();
    }

    // ──────────────── PackageProcessor ────────────────

    [Fact]
    public void Package_CanProcess_MsiAndMsix()
    {
        var pp = new PackageProcessor();
        pp.CanProcess(Script(".msi")).Should().BeTrue();
        pp.CanProcess(Script(".msix")).Should().BeTrue();
    }

    [Fact]
    public void Package_CannotProcess_Other()
    {
        var pp = new PackageProcessor();
        pp.CanProcess(Script(".ps1")).Should().BeFalse();
        pp.CanProcess(Script(".exe")).Should().BeFalse();
    }

    // ──────────────── Routing coverage: every extension has exactly one processor ────────────────

    [Theory]
    [InlineData(".ps1")]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    [InlineData(".exe")]
    [InlineData(".msi")]
    [InlineData(".msix")]
    public void EachExtension_HasExactlyOneProcessor(string extension)
    {
        var processors = new StartSet.Engine.Interfaces.IScriptProcessor[]
        {
            new PowerShellProcessor(),
            new BatchProcessor(),
            new ExecutableProcessor(),
            new PackageProcessor()
        };

        var script = Script(extension);
        var matches = processors.Where(p => p.CanProcess(script)).ToList();

        matches.Should().HaveCount(1, $"extension {extension} should be handled by exactly one processor");
    }

    [Theory]
    [InlineData(".py")]
    [InlineData(".sh")]
    [InlineData(".vbs")]
    [InlineData(".txt")]
    public void UnsupportedExtension_NoProcessor(string extension)
    {
        var processors = new StartSet.Engine.Interfaces.IScriptProcessor[]
        {
            new PowerShellProcessor(),
            new BatchProcessor(),
            new ExecutableProcessor(),
            new PackageProcessor()
        };

        var script = Script(extension);
        processors.Any(p => p.CanProcess(script)).Should().BeFalse(
            $"extension {extension} should not be handled by any processor");
    }
}
