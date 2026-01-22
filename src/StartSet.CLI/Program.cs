using System.CommandLine;
using System.Reflection;
using StartSet.CLI.Commands;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;

namespace StartSet.CLI;

/// <summary>
/// StartSet CLI entry point.
/// Windows port of macadmins/outset.
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Handle --version flag before anything else
        if (args.Length == 1 && (args[0] == "--version" || args[0] == "-V"))
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Console.WriteLine(version != null ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}" : "0.0.0.0");
            return 0;
        }

        // Initialize logging
        var preferencesService = new PreferencesService();
        preferencesService.Load();
        StartSetLogger.Initialize(preferencesService.Preferences, isService: false);

        try
        {
            // Build root command
            var rootCommand = BuildRootCommand(preferencesService);

            // Execute command
            return await rootCommand.InvokeAsync(args);
        }
        catch (Exception ex)
        {
            StartSetLogger.Fatal(ex, "Unhandled exception in StartSet CLI");
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }
        finally
        {
            StartSetLogger.CloseAndFlush();
        }
    }

    private static RootCommand BuildRootCommand(PreferencesService preferencesService)
    {
        var rootCommand = new RootCommand("StartSet - Windows script automation at boot, login, and on-demand")
        {
            Name = "startset"
        };

        // Global options
        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Enable verbose logging");

        var debugOption = new Option<bool>(
            aliases: ["--debug", "-d"],
            description: "Enable debug logging");

        rootCommand.AddGlobalOption(verboseOption);
        rootCommand.AddGlobalOption(debugOption);

        // Add commands
        rootCommand.AddCommand(BootCommand.Create(preferencesService));
        rootCommand.AddCommand(LoginCommand.Create(preferencesService));
        rootCommand.AddCommand(LoginWindowCommand.Create(preferencesService));
        rootCommand.AddCommand(LoginPrivilegedCommand.Create(preferencesService));
        rootCommand.AddCommand(OnDemandCommand.Create(preferencesService));
        rootCommand.AddCommand(CleanupCommand.Create(preferencesService));
        rootCommand.AddCommand(ProcessingCommand.Create(preferencesService));
        rootCommand.AddCommand(AddCommand.Create(preferencesService));
        rootCommand.AddCommand(RemoveCommand.Create(preferencesService));
        rootCommand.AddCommand(ListCommand.Create(preferencesService));

        // Ignored user commands (matching outset)
        rootCommand.AddCommand(IgnoredUserCommand.CreateAdd(preferencesService));
        rootCommand.AddCommand(IgnoredUserCommand.CreateRemove(preferencesService));
        rootCommand.AddCommand(IgnoredUserCommand.CreateList(preferencesService));

        // Override commands (matching outset)
        rootCommand.AddCommand(OverrideCommand.CreateAdd(preferencesService));
        rootCommand.AddCommand(OverrideCommand.CreateRemove(preferencesService));
        rootCommand.AddCommand(OverrideCommand.CreateList(preferencesService));

        // Checksum command (matching outset)
        rootCommand.AddCommand(ChecksumCommand.Create(preferencesService));

        return rootCommand;
    }
}
