using System.CommandLine;
using System.Text;
using StartSet.Core.Constants;
using StartSet.Core.Enums;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Tracking;
using StartSet.Infrastructure.Validation;

namespace StartSet.CLI.Commands;

/// <summary>
/// Command to list scripts in payload directories.
/// </summary>
public static class ListCommand
{
    public static Command Create(PreferencesService preferencesService)
    {
        var command = new Command("list", "List scripts in payload directories");

        var typeOption = new Option<string?>(
            aliases: ["--type", "-t"],
            description: "Filter by payload type (omit to show all)");

        var showExecutedOption = new Option<bool>(
            aliases: ["--show-executed", "-e"],
            description: "Show run-once execution status");

        var jsonOption = new Option<bool>(
            aliases: ["--json", "-j"],
            description: "Output in JSON format");

        command.AddOption(typeOption);
        command.AddOption(showExecutedOption);
        command.AddOption(jsonOption);

        command.SetHandler((type, showExecuted, json) =>
        {
            var payloadTypes = type != null
                ? new[] { ParsePayloadType(type) }.Where(p => p != null).Cast<PayloadType>().ToArray()
                : Enum.GetValues<PayloadType>();

            if (type != null && payloadTypes.Length == 0)
            {
                Console.Error.WriteLine($"Unknown payload type: {type}");
                Environment.ExitCode = 1;
                return;
            }

            var results = new List<ScriptListEntry>();

            foreach (var payloadType in payloadTypes)
            {
                var directory = payloadType.GetDirectoryPath();
                if (!Directory.Exists(directory))
                    continue;

                RunOnceTracker? tracker = null;
                if (showExecuted && payloadType.IsRunOnce())
                {
                    tracker = new RunOnceTracker(payloadType.IsUserContext() ? Environment.UserName : null);
                }

                var files = Directory.GetFiles(directory);
                foreach (var file in files.OrderBy(f => f))
                {
                    var entry = new ScriptListEntry
                    {
                        PayloadType = payloadType.ToString(),
                        FileName = Path.GetFileName(file),
                        FilePath = file,
                        Size = new FileInfo(file).Length
                    };

                    if (tracker != null)
                    {
                        var checksum = ChecksumService.ComputeChecksum(file);
                        entry.Executed = tracker.HasExecuted(file, checksum);
                    }

                    results.Add(entry);
                }
            }

            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                PrintTable(results, showExecuted);
            }
        }, typeOption, showExecutedOption, jsonOption);

        return command;
    }

    private static void PrintTable(List<ScriptListEntry> entries, bool showExecuted)
    {
        if (entries.Count == 0)
        {
            Console.WriteLine("No scripts found.");
            return;
        }

        // Group by payload type
        var grouped = entries.GroupBy(e => e.PayloadType).OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {group.Key} ({Paths.ScriptRoot}\\{group.Key.ToLowerInvariant().Replace("privileged", "-privileged")}) ===");
            Console.WriteLine();

            var header = showExecuted
                ? $"{"Name",-40} {"Size",-10} {"Executed",-10}"
                : $"{"Name",-40} {"Size",-10}";
            Console.WriteLine(header);
            Console.WriteLine(new string('-', header.Length));

            foreach (var entry in group.OrderBy(e => e.FileName))
            {
                var size = FormatSize(entry.Size);
                var line = showExecuted
                    ? $"{entry.FileName,-40} {size,-10} {(entry.Executed == true ? "Yes" : "No"),-10}"
                    : $"{entry.FileName,-40} {size,-10}";
                Console.WriteLine(line);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {entries.Count} script(s)");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
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

    private class ScriptListEntry
    {
        public required string PayloadType { get; set; }
        public required string FileName { get; set; }
        public required string FilePath { get; set; }
        public long Size { get; set; }
        public bool? Executed { get; set; }
    }
}
