using System.IO;
using FluentAssertions;
using StartSet.Core.Enums;
using StartSet.Core.Models;
using StartSet.Engine;
using StartSet.Infrastructure.Configuration;
using StartSet.Tests.Helpers;

namespace StartSet.Tests.Integration;

/// <summary>
/// Integration tests exercising ExecutionEngine with real temp directories and scripts.
/// These tests create real script files, configure preferences pointing to those directories,
/// and validate the full discovery → execute → track pipeline.
///
/// The engine is constructed with the temp root, so discovery stays inside it.
/// Without that the engine reads the installed payload directories of whatever
/// machine the suite runs on, and an integration test becomes a way to execute
/// production login payloads by accident.
/// </summary>
public class ExecutionEngineIntegrationTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private PreferencesService CreatePreferencesService(
        bool waitForNetwork = false,
        bool logScriptOutput = false,
        bool checksumValidation = false,
        int scriptTimeout = 30,
        List<string>? ignoredUsers = null,
        List<string>? overrides = null)
    {
        var prefs = new StartSetPreferences
        {
            WaitForNetwork = waitForNetwork,
            IgnoreNetworkFailure = true,
            LogScriptOutput = logScriptOutput,
            ChecksumValidation = checksumValidation,
            ScriptTimeout = scriptTimeout,
            IgnoredUsers = ignoredUsers ?? [],
            Overrides = overrides ?? []
        };

        var configPath = _temp.CreateFile("Config.yaml", "");
        var service = new PreferencesService();
        service.Save(prefs, configPath);
        service.Load(configPath);
        return service;
    }

    // ──────────────── Script Discovery ────────────────

    [Fact]
    public async Task ExecuteAsync_EmptyDirectory_ReturnsEmptyResults()
    {
        var service = CreatePreferencesService();
        var engine = new ExecutionEngine(service, _temp.Path);

        // Point at a directory with no scripts — directories won't exist under temp
        // but ExecutionEngine tolerates missing directories gracefully
        var results = await engine.ExecuteAsync([PayloadType.BootEvery], waitForNetwork: false);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_DiscoversPayloadsUnderTheSuppliedRoot()
    {
        // Every other test here asserts emptiness, which a regression that went
        // back to the installed ProgramData paths would still satisfy on a build
        // agent -- those directories are empty there too. This one fails if
        // discovery stops honouring the supplied root, because the only place the
        // script exists is under it.
        //
        // boot-every runs in the service's own context, so this exercises the
        // discovery and execution pipeline without needing a user session.
        var service = CreatePreferencesService();
        _temp.CreateFile(Path.Combine("boot-every", "Marker.ps1"), "exit 0");
        var engine = new ExecutionEngine(service, _temp.Path);

        var results = await engine.ExecuteAsync([PayloadType.BootEvery], waitForNetwork: false);

        // Discovery is the claim under test, so assert the script was found -- if
        // the root were ignored there would be no result at all.
        //
        // It is Skipped rather than Success on purpose: boot-every requires
        // elevation, so the permission validator checks that the payload sits in a
        // directory only administrators can write, and a temp directory correctly
        // fails that. Asserting Success here would mean weakening a security
        // control to suit a test.
        results.Should().ContainSingle();
        results[0].Script.FileName.Should().Be("Marker.ps1");
        results[0].Status.Should().Be(ExecutionStatus.Skipped);
        results[0].ErrorMessage.Should().Contain("Permission validation failed");
    }

    // ──────────────── Ignored Users ────────────────

    [Fact]
    public async Task ExecuteAsync_IgnoredUser_SkipsExecution()
    {
        var service = CreatePreferencesService(ignoredUsers: ["testuser"]);
        var engine = new ExecutionEngine(service, _temp.Path);

        var results = await engine.ExecuteAsync(
            [PayloadType.LoginEvery],
            username: "testuser",
            waitForNetwork: false);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_IgnoredUser_CaseInsensitive()
    {
        var service = CreatePreferencesService(ignoredUsers: ["TestUser"]);
        var engine = new ExecutionEngine(service, _temp.Path);

        var results = await engine.ExecuteAsync(
            [PayloadType.LoginEvery],
            username: "testuser",
            waitForNetwork: false);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_NonIgnoredUser_DoesNotSkip()
    {
        var service = CreatePreferencesService(ignoredUsers: ["blockeduser"]);
        var engine = new ExecutionEngine(service, _temp.Path);

        // This user is NOT in the ignored list, so the engine won't skip.
        // The engine reads the temp root, which has no login-every directory, so
        // there is nothing to run and the result is empty because of that -- not
        // because the user was ignored.
        //
        // This used to read the real payload directory and assert emptiness on the
        // stated assumption that "there are no scripts in the actual directories".
        // That is a property of the machine, not of the code: on any machine with
        // payloads installed it executed real login scripts for effect and then
        // failed the assertion.
        var results = await engine.ExecuteAsync(
            [PayloadType.LoginEvery],
            username: "alloweduser",
            waitForNetwork: false);

        results.Should().BeEmpty();
    }

    // ──────────────── Cancellation ────────────────

    [Fact]
    public async Task ExecuteAsync_CancelledToken_ReturnsPartialResults()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var service = CreatePreferencesService();
        var engine = new ExecutionEngine(service, _temp.Path);

        var results = await engine.ExecuteAsync(
            [PayloadType.BootEvery, PayloadType.BootOnce],
            cancellationToken: cts.Token);

        // Should stop early (empty or partial)
        results.Should().NotBeNull();
    }

    // ──────────────── EnsureDirectories ────────────────

    [Fact]
    public void EnsureDirectories_CreatesAllPayloadDirectories()
    {
        // This attempts to create directories under C:\ProgramData\ManagedState
        // which requires elevation. Test that it doesn't throw even if dirs exist.
        var act = () => ExecutionEngine.EnsureDirectories();
        act.Should().NotThrow();
    }

    // ──────────────── CleanupTriggerFiles ────────────────

    [Fact]
    public void CleanupTriggerFiles_DoesNotThrow()
    {
        var act = () => ExecutionEngine.CleanupTriggerFiles();
        act.Should().NotThrow();
    }

    // ──────────────── Multiple PayloadTypes ────────────────

    [Fact]
    public async Task ExecuteAsync_MultiplePayloadTypes_ProcessesAll()
    {
        var service = CreatePreferencesService();
        var engine = new ExecutionEngine(service, _temp.Path);

        // With no scripts on disk, all types should return empty but not throw
        var results = await engine.ExecuteAsync(
            [PayloadType.BootEvery, PayloadType.LoginEvery, PayloadType.OnDemand],
            waitForNetwork: false);

        results.Should().NotBeNull();
    }
}
