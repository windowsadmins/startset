using System.CommandLine;
using StartSet.Core.Constants;
using StartSet.Core.Enums;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;
using StartSet.Infrastructure.Tracking;

namespace StartSet.CLI.Commands;

/// <summary>
/// Command to remove scripts from payload directories.
/// </summary>
public static class RemoveCommand
{
    public static Command Create(PreferencesService preferencesService)
    {
        var command = new Command("remove", "Remove a script from a payload directory");

        var scriptArgument = new Argument<string>(
            name: "script",
            description: "Name of the script file to remove");

        var typeOption = new Option<string>(
            aliases: ["--type", "-t"],
            description: "Payload type to remove script from")
        { IsRequired = true };

        var clearRunOnceOption = new Option<bool>(
            aliases: ["--clear-runonce"],
            description: "Also clear run-once tracking for this script");

        command.AddArgument(scriptArgument);
        command.AddOption(typeOption);
        command.AddOption(clearRunOnceOption);

        command.SetHandler((scriptName, type, clearRunOnce) =>
        {
            var payloadType = ParsePayloadType(type);
            if (payloadType == null)
            {
                Console.Error.WriteLine($"Unknown payload type: {type}");
                Environment.ExitCode = 1;
                return;
            }

            var targetDir = payloadType.Value.GetDirectoryPath();
            var targetPath = Path.Combine(targetDir, scriptName);

            try
            {
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                    StartSetLogger.Information("Removed script {Script} from {Type}", scriptName, type);
                    Console.WriteLine($"Removed: {targetPath}");
                }
                else
                {
                    Console.WriteLine($"Script not found: {targetPath}");
                }

                // Clear run-once tracking if requested
                if (clearRunOnce && payloadType.Value.IsRunOnce())
                {
                    // Clear for system
                    var systemTracker = new RunOnceTracker(null);
                    if (systemTracker.ClearExecution(targetPath))
                    {
                        Console.WriteLine("Cleared system run-once tracking");
                    }

                    // Clear for current user
                    var userTracker = new RunOnceTracker(Environment.UserName);
                    if (userTracker.ClearExecution(targetPath))
                    {
                        Console.WriteLine($"Cleared run-once tracking for user: {Environment.UserName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error removing script: {ex.Message}");
                StartSetLogger.Error(ex, "Failed to remove script {Script} from {Type}", scriptName, type);
                Environment.ExitCode = 1;
            }
        }, scriptArgument, typeOption, clearRunOnceOption);

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
        "on-demand-privileged" => PayloadType.OnDemandPrivileged,
        _ => null
    };
}
