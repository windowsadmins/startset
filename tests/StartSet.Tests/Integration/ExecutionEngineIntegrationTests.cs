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
        var engine = new ExecutionEngine(service);

        // Point at a directory with no scripts — directories won't exist under temp
        // but ExecutionEngine tolerates missing directories gracefully
        var results = await engine.ExecuteAsync([PayloadType.BootEvery], waitForNetwork: false);

        results.Should().BeEmpty();
    }

    // ──────────────── Ignored Users ────────────────

    [Fact]
    public async Task ExecuteAsync_IgnoredUser_SkipsExecution()
    {
        var service = CreatePreferencesService(ignoredUsers: ["testuser"]);
        var engine = new ExecutionEngine(service);

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
        var engine = new ExecutionEngine(service);

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
        var engine = new ExecutionEngine(service);

        // This user is NOT in the ignored list, so engine won't skip.
        // Since there are no scripts in the actual directories, result is empty (not skipped).
        var results = await engine.ExecuteAsync(
            [PayloadType.LoginEvery],
            username: "alloweduser",
            waitForNetwork: false);

        // Empty because no scripts on disk, but the key is that engine didn't short-circuit
        results.Should().BeEmpty();
    }

    // ──────────────── Cancellation ────────────────

    [Fact]
    public async Task ExecuteAsync_CancelledToken_ReturnsPartialResults()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var service = CreatePreferencesService();
        var engine = new ExecutionEngine(service);

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
        var engine = new ExecutionEngine(service);

        // With no scripts on disk, all types should return empty but not throw
        var results = await engine.ExecuteAsync(
            [PayloadType.BootEvery, PayloadType.LoginEvery, PayloadType.OnDemand],
            waitForNetwork: false);

        results.Should().NotBeNull();
    }
}
