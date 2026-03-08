using System.CommandLine;
using StartSet.Core.Enums;
using StartSet.Engine;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;

namespace StartSet.CLI.Commands;

/// <summary>
/// Command to run on-demand scripts.
/// </summary>
public static class OnDemandCommand
{
    public static Command Create(PreferencesService preferencesService)
    {
        var command = new Command("on-demand", "Run on-demand scripts");

        var privilegedOption = new Option<bool>(
            aliases: ["--privileged", "-p"],
            description: "Run on-demand-privileged scripts instead");

        command.AddOption(privilegedOption);

        command.SetHandler(async (privileged) =>
        {
            var payloadType = privileged ? PayloadType.OnDemandPrivileged : PayloadType.OnDemand;
            StartSetLogger.Information("Starting on-demand script execution (privileged: {Privileged})", privileged);

            var engine = new ExecutionEngine(preferencesService);
            var results = await engine.ExecuteAsync(
                [payloadType],
                username: Environment.UserName,
                waitForNetwork: false);

            var failed = results.Count(r => r.Status == ExecutionStatus.Failed);
            Environment.ExitCode = failed > 0 ? 1 : 0;
        }, privilegedOption);

        return command;
    }
}
