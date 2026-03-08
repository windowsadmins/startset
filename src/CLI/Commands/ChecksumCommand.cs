using System.CommandLine;
using StartSet.Core.Constants;
using StartSet.Core.Enums;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;
using StartSet.Infrastructure.Validation;

namespace StartSet.CLI.Commands;

/// <summary>
/// Command to compute and manage script checksums.
/// Matches outset's --checksum functionality.
/// </summary>
public static class ChecksumCommand
{
    public static Command Create(PreferencesService preferencesService)
    {
        var command = new Command("checksum", "Compute SHA256 checksum of files or generate checksum configuration");

        var fileArgument = new Argument<string>(
            name: "file",
            description: "Path to file, or 'all' to compute checksums for all scripts in payload directories");

        var recordOption = new Option<bool>(
            aliases: ["--record", "-r"],
            description: "Record the checksum to the checksums file");

        var commentOption = new Option<string?>(
            aliases: ["--comment", "-c"],
            description: "Optional comment for recorded checksum");

        command.AddArgument(fileArgument);
        command.AddOption(recordOption);
        command.AddOption(commentOption);

        command.SetHandler((fileOrKeyword, record, comment) =>
        {
            try
            {
                if (fileOrKeyword.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    ComputeAllChecksums(record, comment);
                }
                else
                {
                    ComputeFileChecksum(fileOrKeyword, record, comment);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                StartSetLogger.Error(ex, "Checksum operation failed");
                Environment.ExitCode = 1;
            }
        }, fileArgument, recordOption, commentOption);

        return command;
    }

    private static void ComputeFileChecksum(string filePath, bool record, string? comment)
    {
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"File not found: {filePath}");
            Environment.ExitCode = 1;
            return;
        }

        var checksum = ChecksumService.ComputeChecksum(filePath);
        var fileName = Path.GetFileName(filePath);

        Console.WriteLine($"{checksum}  {fileName}");

        if (record)
        {
            var checksumService = new ChecksumService();
            checksumService.RecordChecksum(filePath, comment ?? $"Recorded via CLI at {DateTime.Now}");
            Console.WriteLine($"Checksum recorded to {Paths.ChecksumFile}");
            StartSetLogger.Information("Recorded checksum for {File}: {Checksum}", filePath, checksum);
        }
    }

    private static void ComputeAllChecksums(bool record, string? comment)
    {
        var prefs = new PreferencesService().Load();
        var allowedExtensions = prefs.AllowedExtensions.Select(e => e.ToLowerInvariant()).ToHashSet();
        var checksumService = record ? new ChecksumService() : null;
        var count = 0;

        Console.WriteLine("# StartSet SHA256 Checksums");
        Console.WriteLine($"# Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();

        // Process each payload type
        foreach (PayloadType payloadType in Enum.GetValues<PayloadType>())
        {
            var directory = payloadType.GetDirectoryPath();
            if (!Directory.Exists(directory))
                continue;

            var files = Directory.GetFiles(directory)
                .Where(f => allowedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f);

            foreach (var filePath in files)
            {
                try
                {
                    var checksum = ChecksumService.ComputeChecksum(filePath);
                    Console.WriteLine($"{checksum}  {filePath}");
                    count++;

                    if (record && checksumService != null)
                    {
                        checksumService.RecordChecksum(filePath, comment ?? $"Recorded via 'checksum all' at {DateTime.Now}");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"# Error computing checksum for {filePath}: {ex.Message}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"# Total: {count} files");

        if (record)
        {
            Console.WriteLine($"# Checksums recorded to {Paths.ChecksumFile}");
            StartSetLogger.Information("Recorded checksums for {Count} files", count);
        }
    }
}
