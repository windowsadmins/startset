using System.CommandLine;
using StartSet.Core.Constants;
using StartSet.Core.Enums;
using StartSet.Engine;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;

namespace StartSet.CLI.Commands;

/// <summary>
/// Command to process specific payload types (for direct invocation).
/// </summary>
public static class ProcessingCommand
{
    public static Command Create(PreferencesService preferencesService)
    {
        var command = new Command("process", "Process a specific payload type");

        var typeArgument = new Argument<string>(
            name: "type",
            description: "Payload type to process (boot-once, boot-every, login-once, login-every, login-window, login-privileged-once, login-privileged-every, on-demand, on-demand-privileged)");

        var usernameOption = new Option<string?>(
            aliases: ["--user", "-u"],
            description: "Username for run-once tracking");

        var skipNetworkOption = new Option<bool>(
            aliases: ["--skip-network"],
            description: "Skip waiting for network connectivity");

        command.AddArgument(typeArgument);
        command.AddOption(usernameOption);
        command.AddOption(skipNetworkOption);

        command.SetHandler(async (type, username, skipNetwork) =>
        {
            var payloadType = ParsePayloadType(type);
            if (payloadType == null)
            {
                Console.Error.WriteLine($"Unknown payload type: {type}");
                Console.Error.WriteLine("Valid types: boot-once, boot-every, login-once, login-every, login-window, login-privileged-once, login-privileged-every, on-demand, on-demand-privileged");
                Environment.ExitCode = 1;
                return;
            }

            StartSetLogger.Information("Processing payload type: {Type}", payloadType);

            var engine = new ExecutionEngine(preferencesService);
            var results = await engine.ExecuteAsync(
                [payloadType.Value],
                username: username ?? (payloadType.Value.IsUserContext() ? Environment.UserName : null),
                waitForNetwork: !skipNetwork && payloadType.Value == PayloadType.BootOnce);

            var failed = results.Count(r => r.Status == ExecutionStatus.Failed);
            Environment.ExitCode = failed > 0 ? 1 : 0;
        }, typeArgument, usernameOption, skipNetworkOption);

        return command;
    }

    private static PayloadType? ParsePayloadType(string type) => type.ToLowerInvariant().Replace("_", "-") switch
    {
        "boot-once" => PayloadType.BootOnce,
        "boot-every" => PayloadType.BootEvery,
        "login-once" => PayloadType.LoginOnce,
        "login-every" => PayloadType.LoginEvery,
        "login-window" => PayloadType.LoginWindow,
        "login-privileged-once" => PayloadType.LoginPrivilegedOnce,
        "login-privileged-every" => PayloadType.LoginPrivilegedEvery,
        "on-demand" => PayloadType.OnDemand,
        "on-demand-privileged" or "ondemand-privileged" => PayloadType.OnDemandPrivileged,
        _ => null
    };
}
