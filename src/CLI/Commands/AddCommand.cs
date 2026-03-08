using System.CommandLine;
using StartSet.Core.Constants;
using StartSet.Core.Enums;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;
using StartSet.Infrastructure.Validation;

namespace StartSet.CLI.Commands;

/// <summary>
/// Command to add scripts to payload directories.
/// </summary>
public static class AddCommand
{
    public static Command Create(PreferencesService preferencesService)
    {
        var command = new Command("add", "Add a script to a payload directory");

        var scriptArgument = new Argument<string>(
            name: "script",
            description: "Path to the script file to add");

        var typeOption = new Option<string>(
            aliases: ["--type", "-t"],
            description: "Payload type to add script to")
        { IsRequired = true };

        var checksumOption = new Option<bool>(
            aliases: ["--checksum", "-c"],
            description: "Record checksum for validation");

        command.AddArgument(scriptArgument);
        command.AddOption(typeOption);
        command.AddOption(checksumOption);

        command.SetHandler((scriptPath, type, recordChecksum) =>
        {
            var payloadType = ParsePayloadType(type);
            if (payloadType == null)
            {
                Console.Error.WriteLine($"Unknown payload type: {type}");
                Environment.ExitCode = 1;
                return;
            }

            if (!File.Exists(scriptPath))
            {
                Console.Error.WriteLine($"Script file not found: {scriptPath}");
                Environment.ExitCode = 1;
                return;
            }

            var targetDir = payloadType.Value.GetDirectoryPath();
            var fileName = Path.GetFileName(scriptPath);
            var targetPath = Path.Combine(targetDir, fileName);

            try
            {
                // Ensure directory exists
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                // Copy script
                File.Copy(scriptPath, targetPath, overwrite: true);
                StartSetLogger.Information("Added script {Script} to {Type}", fileName, type);
                Console.WriteLine($"Added: {targetPath}");

                // Record checksum if requested
                if (recordChecksum)
                {
                    var checksumService = new ChecksumService();
                    checksumService.RecordChecksum(targetPath, $"Added via CLI at {DateTime.Now}");
                    Console.WriteLine($"Checksum recorded: {ChecksumService.ComputeChecksum(targetPath)}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error adding script: {ex.Message}");
                StartSetLogger.Error(ex, "Failed to add script {Script} to {Type}", scriptPath, type);
                Environment.ExitCode = 1;
            }
        }, scriptArgument, typeOption, checksumOption);

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
