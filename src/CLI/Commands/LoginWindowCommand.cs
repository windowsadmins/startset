using System.CommandLine;
using StartSet.Core.Enums;
using StartSet.Engine;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;

namespace StartSet.CLI.Commands;

/// <summary>
/// Command to run login-window scripts.
/// </summary>
public static class LoginWindowCommand
{
    public static Command Create(PreferencesService preferencesService)
    {
        var command = new Command("login-window", "Run login-window scripts (before user authentication)");

        command.SetHandler(async () =>
        {
            StartSetLogger.Information("Starting login-window script execution");

            var engine = new ExecutionEngine(preferencesService);
            var results = await engine.ExecuteAsync(
                [PayloadType.LoginWindow],
                username: null,
                waitForNetwork: false);

            var failed = results.Count(r => r.Status == ExecutionStatus.Failed);
            Environment.ExitCode = failed > 0 ? 1 : 0;
        });

        return command;
    }
}
