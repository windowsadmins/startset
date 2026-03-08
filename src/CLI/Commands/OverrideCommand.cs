using System.CommandLine;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;
using StartSet.Infrastructure.Tracking;

namespace StartSet.CLI.Commands;

/// <summary>
/// Commands to manage script overrides.
/// Matches outset's --add-override and --remove-override functionality.
/// Overrides force re-execution of run-once scripts.
/// </summary>
public static class OverrideCommand
{
    public static Command CreateAdd(PreferencesService preferencesService)
    {
        var command = new Command("add-override", "Add one or more scripts to the override list (force re-run)");

        var scriptArgument = new Argument<string[]>(
            name: "scripts",
            description: "One or more script paths or filenames to add to override list")
        { Arity = ArgumentArity.OneOrMore };

        command.AddArgument(scriptArgument);

        command.SetHandler((scripts) =>
        {
            var prefs = preferencesService.Preferences;
            var added = new List<string>();
            var skipped = new List<string>();

            foreach (var script in scripts)
            {
                var scriptKey = NormalizeScriptKey(script);
                if (string.IsNullOrWhiteSpace(scriptKey))
                    continue;

                if (prefs.Overrides.Contains(scriptKey, StringComparer.OrdinalIgnoreCase))
                {
                    skipped.Add(scriptKey);
                }
                else
                {
                    prefs.Overrides.Add(scriptKey);
                    added.Add(scriptKey);
                }
            }

            if (added.Count > 0)
            {
                try
                {
                    preferencesService.Save(prefs);
                    foreach (var script in added)
                    {
                        Console.WriteLine($"Added override: {script}");
                        StartSetLogger.Information("Added script override: {Script}", script);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error saving preferences: {ex.Message}");
                    StartSetLogger.Error(ex, "Failed to save preferences after adding overrides");
                    Environment.ExitCode = 1;
                    return;
                }
            }

            foreach (var script in skipped)
            {
                Console.WriteLine($"Script already in overrides: {script}");
            }

            if (added.Count == 0 && skipped.Count == 0)
            {
                Console.WriteLine("No valid scripts provided.");
                Environment.ExitCode = 1;
            }
        }, scriptArgument);

        return command;
    }

    public static Command CreateRemove(PreferencesService preferencesService)
    {
        var command = new Command("remove-override", "Remove one or more scripts from the override list");

        var scriptArgument = new Argument<string[]>(
            name: "scripts",
            description: "One or more script paths or filenames to remove from override list")
        { Arity = ArgumentArity.OneOrMore };

        var clearRunOnceOption = new Option<bool>(
            aliases: ["--clear-runonce", "-c"],
            description: "Also clear the script from run-once tracking (will run again next time)");

        var usernameOption = new Option<string?>(
            aliases: ["--user", "-u"],
            description: "Username for clearing user-specific run-once tracking");

        command.AddArgument(scriptArgument);
        command.AddOption(clearRunOnceOption);
        command.AddOption(usernameOption);

        command.SetHandler((scripts, clearRunOnce, username) =>
        {
            var prefs = preferencesService.Preferences;
            var removed = new List<string>();
            var notFound = new List<string>();

            foreach (var script in scripts)
            {
                var scriptKey = NormalizeScriptKey(script);
                if (string.IsNullOrWhiteSpace(scriptKey))
                    continue;

                var existingScript = prefs.Overrides.FirstOrDefault(s =>
                    string.Equals(s, scriptKey, StringComparison.OrdinalIgnoreCase));

                if (existingScript != null)
                {
                    prefs.Overrides.Remove(existingScript);
                    removed.Add(scriptKey);
                }
                else
                {
                    notFound.Add(scriptKey);
                }

                // Optionally clear from run-once tracking
                if (clearRunOnce)
                {
                    try
                    {
                        var tracker = new RunOnceTracker(username);
                        if (tracker.ClearExecution(scriptKey))
                        {
                            Console.WriteLine($"Cleared run-once tracking for: {scriptKey}");
                            StartSetLogger.Information("Cleared run-once tracking for: {Script}", scriptKey);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Warning: Could not clear run-once tracking: {ex.Message}");
                    }
                }
            }

            if (removed.Count > 0)
            {
                try
                {
                    preferencesService.Save(prefs);
                    foreach (var script in removed)
                    {
                        Console.WriteLine($"Removed override: {script}");
                        StartSetLogger.Information("Removed script override: {Script}", script);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error saving preferences: {ex.Message}");
                    StartSetLogger.Error(ex, "Failed to save preferences after removing overrides");
                    Environment.ExitCode = 1;
                    return;
                }
            }

            foreach (var script in notFound)
            {
                Console.WriteLine($"Script not in overrides: {script}");
            }

            if (removed.Count == 0 && notFound.Count == 0)
            {
                Console.WriteLine("No valid scripts provided.");
                Environment.ExitCode = 1;
            }
        }, scriptArgument, clearRunOnceOption, usernameOption);

        return command;
    }

    public static Command CreateList(PreferencesService preferencesService)
    {
        var command = new Command("list-overrides", "List all script overrides");

        command.SetHandler(() =>
        {
            var prefs = preferencesService.Preferences;

            if (prefs.Overrides.Count == 0)
            {
                Console.WriteLine("No overrides configured.");
                return;
            }

            Console.WriteLine("Script overrides (will re-run even if previously executed):");
            foreach (var script in prefs.Overrides.OrderBy(s => s))
            {
                Console.WriteLine($"  {script}");
            }
        });

        return command;
    }

    /// <summary>
    /// Normalizes a script path to just the filename for matching.
    /// </summary>
    private static string NormalizeScriptKey(string script)
    {
        // Use just the filename for matching (consistent with RunOnceTracker)
        return Path.GetFileName(script.Trim()).ToLowerInvariant();
    }
}
