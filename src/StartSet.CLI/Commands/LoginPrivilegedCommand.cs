using System.CommandLine;
using StartSet.Core.Enums;
using StartSet.Engine;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;

namespace StartSet.CLI.Commands;

/// <summary>
/// Command to run login-privileged scripts.
/// </summary>
public static class LoginPrivilegedCommand
{
    public static Command Create(PreferencesService preferencesService)
    {
        var command = new Command("login-privileged", "Run login-privileged scripts (with elevation)");

        var usernameOption = new Option<string?>(
            aliases: ["--user", "-u"],
            description: "Username for run-once tracking (defaults to current user)");

        command.AddOption(usernameOption);

        command.SetHandler(async (username) =>
        {
            var user = username ?? Environment.UserName;
            StartSetLogger.Information("Starting login-privileged script execution for user: {User}", user);

            var engine = new ExecutionEngine(preferencesService);
            var results = await engine.ExecuteAsync(
                [PayloadType.LoginPrivilegedOnce, PayloadType.LoginPrivilegedEvery],
                username: user,
                waitForNetwork: false);

            var failed = results.Count(r => r.Status == ExecutionStatus.Failed);
            Environment.ExitCode = failed > 0 ? 1 : 0;
        }, usernameOption);

        return command;
    }
}
