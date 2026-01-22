using System.CommandLine;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Logging;

namespace StartSet.CLI.Commands;

/// <summary>
/// Commands to manage ignored users list.
/// Matches outset's --add-ignored-user and --remove-ignored-user functionality.
/// </summary>
public static class IgnoredUserCommand
{
    public static Command CreateAdd(PreferencesService preferencesService)
    {
        var command = new Command("add-ignored-user", "Add one or more users to the ignored list");

        var usernameArgument = new Argument<string[]>(
            name: "usernames",
            description: "One or more usernames to add to ignored list")
        { Arity = ArgumentArity.OneOrMore };

        command.AddArgument(usernameArgument);

        command.SetHandler((usernames) =>
        {
            var prefs = preferencesService.Preferences;
            var added = new List<string>();
            var skipped = new List<string>();

            foreach (var username in usernames)
            {
                var normalizedUsername = username.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(normalizedUsername))
                    continue;

                if (prefs.IgnoredUsers.Contains(normalizedUsername, StringComparer.OrdinalIgnoreCase))
                {
                    skipped.Add(normalizedUsername);
                }
                else
                {
                    prefs.IgnoredUsers.Add(normalizedUsername);
                    added.Add(normalizedUsername);
                }
            }

            if (added.Count > 0)
            {
                try
                {
                    preferencesService.Save(prefs);
                    foreach (var user in added)
                    {
                        Console.WriteLine($"Added ignored user: {user}");
                        StartSetLogger.Information("Added ignored user: {User}", user);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error saving preferences: {ex.Message}");
                    StartSetLogger.Error(ex, "Failed to save preferences after adding ignored users");
                    Environment.ExitCode = 1;
                    return;
                }
            }

            foreach (var user in skipped)
            {
                Console.WriteLine($"User already ignored: {user}");
            }

            if (added.Count == 0 && skipped.Count == 0)
            {
                Console.WriteLine("No valid usernames provided.");
                Environment.ExitCode = 1;
            }
        }, usernameArgument);

        return command;
    }

    public static Command CreateRemove(PreferencesService preferencesService)
    {
        var command = new Command("remove-ignored-user", "Remove one or more users from the ignored list");

        var usernameArgument = new Argument<string[]>(
            name: "usernames",
            description: "One or more usernames to remove from ignored list")
        { Arity = ArgumentArity.OneOrMore };

        command.AddArgument(usernameArgument);

        command.SetHandler((usernames) =>
        {
            var prefs = preferencesService.Preferences;
            var removed = new List<string>();
            var notFound = new List<string>();

            foreach (var username in usernames)
            {
                var normalizedUsername = username.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(normalizedUsername))
                    continue;

                var existingUser = prefs.IgnoredUsers.FirstOrDefault(u =>
                    string.Equals(u, normalizedUsername, StringComparison.OrdinalIgnoreCase));

                if (existingUser != null)
                {
                    prefs.IgnoredUsers.Remove(existingUser);
                    removed.Add(normalizedUsername);
                }
                else
                {
                    notFound.Add(normalizedUsername);
                }
            }

            if (removed.Count > 0)
            {
                try
                {
                    preferencesService.Save(prefs);
                    foreach (var user in removed)
                    {
                        Console.WriteLine($"Removed ignored user: {user}");
                        StartSetLogger.Information("Removed ignored user: {User}", user);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error saving preferences: {ex.Message}");
                    StartSetLogger.Error(ex, "Failed to save preferences after removing ignored users");
                    Environment.ExitCode = 1;
                    return;
                }
            }

            foreach (var user in notFound)
            {
                Console.WriteLine($"User not in ignored list: {user}");
            }

            if (removed.Count == 0 && notFound.Count == 0)
            {
                Console.WriteLine("No valid usernames provided.");
                Environment.ExitCode = 1;
            }
        }, usernameArgument);

        return command;
    }

    public static Command CreateList(PreferencesService preferencesService)
    {
        var command = new Command("list-ignored-users", "List all ignored users");

        command.SetHandler(() =>
        {
            var prefs = preferencesService.Preferences;

            if (prefs.IgnoredUsers.Count == 0)
            {
                Console.WriteLine("No ignored users configured.");
                return;
            }

            Console.WriteLine("Ignored users:");
            foreach (var user in prefs.IgnoredUsers.OrderBy(u => u))
            {
                Console.WriteLine($"  {user}");
            }
        });

        return command;
    }
}
