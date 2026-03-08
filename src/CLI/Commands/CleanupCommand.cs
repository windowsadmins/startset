using System.CommandLine;
using StartSet.Core.Constants;
using StartSet.Engine;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;

namespace StartSet.CLI.Commands;

/// <summary>
/// Command to clean up on-demand directory.
/// </summary>
public static class CleanupCommand
{
    public static Command Create(PreferencesService preferencesService)
    {
        var command = new Command("cleanup", "Clean up on-demand directories and trigger files");

        var allOption = new Option<bool>(
            aliases: ["--all", "-a"],
            description: "Clean up all trigger files and empty on-demand directories");

        command.AddOption(allOption);

        command.SetHandler((all) =>
        {
            StartSetLogger.Information("Starting cleanup (all: {All})", all);

            // Clean up trigger files
            ExecutionEngine.CleanupTriggerFiles();

            if (all)
            {
                // Clean up empty on-demand scripts
                CleanupOnDemandDirectories();
            }

            StartSetLogger.Information("Cleanup complete");
        }, allOption);

        return command;
    }

    private static void CleanupOnDemandDirectories()
    {
        var onDemandDirs = new[]
        {
            Paths.OnDemandDir,
            Paths.OnDemandPrivilegedDir
        };

        foreach (var dir in onDemandDirs)
        {
            try
            {
                if (!Directory.Exists(dir))
                    continue;

                var files = Directory.GetFiles(dir);
                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                        StartSetLogger.Debug("Deleted on-demand script: {File}", file);
                    }
                    catch (Exception ex)
                    {
                        StartSetLogger.Warning("Failed to delete {File}: {Error}", file, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                StartSetLogger.Warning("Error cleaning directory {Dir}: {Error}", dir, ex.Message);
            }
        }
    }
}
