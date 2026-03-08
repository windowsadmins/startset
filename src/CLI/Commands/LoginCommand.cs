using System.CommandLine;
using StartSet.Core.Enums;
using StartSet.Engine;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;

namespace StartSet.CLI.Commands;

/// <summary>
/// Command to run login scripts (login-once and login-every).
/// </summary>
public static class LoginCommand
{
    public static Command Create(PreferencesService preferencesService)
    {
        var command = new Command("login", "Run login scripts (login-once and login-every)");

        var usernameOption = new Option<string?>(
            aliases: ["--user", "-u"],
            description: "Username for run-once tracking (defaults to current user)");

        command.AddOption(usernameOption);

        command.SetHandler(async (username) =>
        {
            var user = username ?? Environment.UserName;
            StartSetLogger.Information("Starting login script execution for user: {User}", user);

            var engine = new ExecutionEngine(preferencesService);
            var results = await engine.ExecuteAsync(
                [PayloadType.LoginOnce, PayloadType.LoginEvery],
                username: user,
                waitForNetwork: false);

            var failed = results.Count(r => r.Status == ExecutionStatus.Failed);
            Environment.ExitCode = failed > 0 ? 1 : 0;
        }, usernameOption);

        return command;
    }
}
