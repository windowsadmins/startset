using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartSet.Core.Constants;
using StartSet.Infrastructure.Logging;

namespace StartSet.App.ViewModels;

/// <summary>
/// ViewModel for the Logs tab: the session tree under C:\ProgramData\ManagedState\logs,
/// one entry per run (logs\YYYY-MM-DD\HHMM-runtype\startset.log).
/// </summary>
public partial class LogsViewModel : ObservableObject
{
    /// <summary>
    /// The installed log tree, or the folder given with --logs: a log tree copied from
    /// another machine can be read the same way.
    /// </summary>
    private static readonly string LogDirectory = LogsArgument() ?? Paths.LogDirectory;

    private static string? LogsArgument()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(args, a => string.Equals(a, "--logs", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    public ObservableCollection<LogFile> LogFiles { get; } = [];

    [ObservableProperty] private LogFile? _selectedLog;
    [ObservableProperty] private string _logContent = string.Empty;
    [ObservableProperty] private string _filterText = string.Empty;

    public IEnumerable<LogLine> FilteredLines
    {
        get
        {
            var lines = LogContent.Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => new LogLine(l.TrimEnd('\r'), ColorForLine(l)));
            if (string.IsNullOrWhiteSpace(FilterText))
                return lines;
            return lines.Where(l => l.Text.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ── Models ───────────────────────────────────────────────────

    public record LogFile(string Name, string Path, DateTime? Date, string RunType, long SizeBytes)
    {
        public string DisplayDate => Date is { } d
            ? $"{d:MMMM} {OrdinalDay(d.Day)} {d:yyyy} at {d:HH:mm}"
            : Name;

        public string DisplayRunType => RunType;

        public string DisplaySize => SizeBytes switch
        {
            < 1024        => $"{SizeBytes} B",
            < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
            _             => $"{SizeBytes / (1024.0 * 1024):0.#} MB",
        };

        private static string OrdinalDay(int day) => (day % 10, day) switch
        {
            (1, not 11) => $"{day}st",
            (2, not 12) => $"{day}nd",
            (3, not 13) => $"{day}rd",
            _           => $"{day}th",
        };
    }

    public record LogLine(string Text, LogLineColor Color);

    public enum LogLineColor { Default, Error, Warning, Success, Debug, Header }

    // ── Refresh ──────────────────────────────────────────────────

    [RelayCommand]
    public void Refresh()
    {
        var selected = SelectedLog?.Path;
        LogFiles.Clear();

        foreach (var entry in SessionLogIndex.List(LogDirectory).Where(e => File.Exists(e.LogPath)))
            LogFiles.Add(new LogFile($"{entry.Day}-{entry.Folder}", entry.LogPath, entry.Started, entry.RunType, FileSize(entry.LogPath)));

        SelectedLog = LogFiles.FirstOrDefault(f => f.Path == selected) ?? LogFiles.FirstOrDefault();
    }

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    // ── Load Content ─────────────────────────────────────────────

    partial void OnSelectedLogChanged(LogFile? value)
    {
        if (value is null)
        {
            LogContent = string.Empty;
            return;
        }

        try
        {
            // Use FileShare.ReadWrite so we can read logs that are being written
            using var fs = new FileStream(value.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            LogContent = reader.ReadToEnd();
        }
        catch
        {
            LogContent = "Unable to read log file.";
        }
    }

    partial void OnLogContentChanged(string value) => OnPropertyChanged(nameof(FilteredLines));
    partial void OnFilterTextChanged(string value) => OnPropertyChanged(nameof(FilteredLines));

    // ── Actions ──────────────────────────────────────────────────

    [RelayCommand]
    private void OpenInEditor()
    {
        if (SelectedLog is null) return;
        Process.Start(new ProcessStartInfo(SelectedLog.Path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var folder = SelectedLog is { } log ? System.IO.Path.GetDirectoryName(log.Path) : LogDirectory;
        if (folder is null || !Directory.Exists(folder)) return;
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static LogLineColor ColorForLine(string line)
    {
        if (HasLevel(line, "ERROR")) return LogLineColor.Error;
        if (HasLevel(line, "WARN")) return LogLineColor.Warning;
        if (HasLevel(line, "DEBUG")) return LogLineColor.Debug;
        if (line.Contains("Execution complete", StringComparison.Ordinal)) return LogLineColor.Success;
        if (line.StartsWith("===") || line.Contains("] ===")) return LogLineColor.Header;
        return LogLineColor.Default;
    }

    /// <summary>
    /// SessionLogger writes "[timestamp] LEVEL message" with an unbracketed, space-padded
    /// level token (INFO/WARN/ERROR/DEBUG); match that token.
    /// </summary>
    private static bool HasLevel(string line, string level)
    {
        var close = line.IndexOf("] ", StringComparison.Ordinal);
        if (close < 0) return false;
        var rest = line.AsSpan(close + 2).TrimStart();
        return rest.StartsWith(level, StringComparison.Ordinal)
            && (rest.Length == level.Length || rest[level.Length] == ' ');
    }
}
