using System.CommandLine;
using StartSet.Core.Enums;
using StartSet.Engine;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;

namespace StartSet.CLI.Commands;

/// <summary>
/// Command to run boot scripts (boot-once and boot-every).
/// </summary>
public static class BootCommand
{
    public static Command Create(PreferencesService preferencesService)
    {
        var command = new Command("boot", "Run boot scripts (boot-once and boot-every)");

        var skipNetworkOption = new Option<bool>(
            aliases: ["--skip-network"],
            description: "Skip waiting for network connectivity");

        command.AddOption(skipNetworkOption);

        command.SetHandler(async (skipNetwork) =>
        {
            StartSetLogger.Information("Starting boot script execution");

            var engine = new ExecutionEngine(preferencesService);
            var results = await engine.ExecuteAsync(
                [PayloadType.BootOnce, PayloadType.BootEvery],
                username: null,
                waitForNetwork: !skipNetwork);

            var failed = results.Count(r => r.Status == ExecutionStatus.Failed);
            Environment.ExitCode = failed > 0 ? 1 : 0;
        }, skipNetworkOption);

        return command;
    }
}
