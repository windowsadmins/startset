using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using StartSet.Core.Constants;
using StartSet.Infrastructure.Gui;
using StartSet.Infrastructure.Logging;
using Microsoft.UI.Dispatching;

namespace StartSet.App.ViewModels;

/// <summary>
/// ViewModel for the Run tab. Starts one of the three runs and streams its output.
/// </summary>
/// <remarks>
/// On demand: drops the trigger file in the triggers folder and follows the service's
/// "trigger" session log -- only the service can start a process in the user's session.
/// Privileged runs: from an elevated window the CLI is started directly and its console
/// output is read as it is written; otherwise it is started through UAC, which cannot
/// redirect output, and its session log is followed instead.
/// </remarks>
public partial class RunViewModel : ObservableObject
{
    private Process? _cliProcess;
    private CancellationTokenSource? _cts;
    private readonly DispatcherQueue _dispatcher;

    public RunViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    // ── Observable State ─────────────────────────────────────────

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int? _lastExitCode;
    [ObservableProperty] private bool _showDebug;
    [ObservableProperty] private RunMode _currentMode;
    [ObservableProperty] private int _payloadCount;
    [ObservableProperty] private string _currentItemName = string.Empty;
    [ObservableProperty] private int _errorCount;

    public bool IsElevated { get; } = PrefsElevation.IsProcessElevated();

    public ObservableCollection<OutputLine> OutputLines { get; } = [];

    public IEnumerable<OutputLine> FilteredLines =>
        ShowDebug ? OutputLines : OutputLines.Where(l => l.Level != LogLevel.Debug);

    // ── Output Line Model ────────────────────────────────────────

    public record OutputLine(string Text, LogLevel Level);

    public enum LogLevel { Info, Debug, Warning, Error, Success }

    // ── Run ──────────────────────────────────────────────────────

    public async Task RunAsync(RunMode mode)
    {
        if (IsRunning) return;

        IsRunning = true;
        CurrentMode = mode;
        LastExitCode = null;
        PayloadCount = 0;
        ErrorCount = 0;
        CurrentItemName = string.Empty;
        OutputLines.Clear();
        OnPropertyChanged(nameof(FilteredLines));

        _cts = new CancellationTokenSource();

        try
        {
            if (RunModes.UsesTrigger(mode))
                await RunThroughServiceAsync(mode, _cts.Token);
            else if (IsElevated)
                await RunCliStreamingAsync(mode, _cts.Token);
            else
                await RunCliElevatedAsync(mode, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLine("[WARN] Stopped. The run itself may still be finishing; see the Logs tab.", LogLevel.Warning);
        }
        catch (System.ComponentModel.Win32Exception ex) when (PrefsElevation.IsElevationCancelled(ex))
        {
            AppendLine("[ERROR] Elevation was cancelled; nothing ran.", LogLevel.Error);
        }
        catch (Exception ex)
        {
            AppendLine($"[ERROR] {ex.Message}", LogLevel.Error);
        }
        finally
        {
            IsRunning = false;
            _cliProcess?.Dispose();
            _cliProcess = null;
        }
    }

    /// <summary>Asks the service for the run through a trigger file and follows its session.</summary>
    private async Task RunThroughServiceAsync(RunMode mode, CancellationToken ct)
    {
        var trigger = RunModes.TriggerPath(mode)!;
        var before = SessionLogIndex.Snapshot();

        try
        {
            using (File.Create(trigger)) { }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            AppendLine($"[ERROR] Could not create {trigger}: {ex.Message}. Is StartSet installed?", LogLevel.Error);
            LastExitCode = 1;
            return;
        }

        AppendLine($"[INFO] Asked the StartSet service for an on-demand run ({trigger})", LogLevel.Info);

        var session = await WaitForSessionAsync(before, RunModes.SessionRunType(mode), TimeSpan.FromSeconds(30), ct);
        if (session is null)
        {
            AppendLine("[ERROR] The StartSet service did not pick up the request within 30 seconds. Check that the service is running.", LogLevel.Error);
            LastExitCode = 1;
            return;
        }

        var status = await TailSessionAsync(session, () => SessionLogIndex.ReadStatus(session) is { } s && s != "running", ct);
        LastExitCode = status == "completed" ? 0 : 1;
    }

    /// <summary>Elevated window: runs the CLI as a child and reads its output as it is written.</summary>
    private async Task RunCliStreamingAsync(RunMode mode, CancellationToken ct)
    {
        var cliPath = FindCliExecutable() ?? throw new FileNotFoundException("managedstatekeeper.exe was not found. Install StartSet or check the install path.");
        var startInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in RunModes.CliArguments(mode))
            startInfo.ArgumentList.Add(arg);

        AppendLine($"[DEBUG] {cliPath} {string.Join(" ", startInfo.ArgumentList)}", LogLevel.Debug);

        _cliProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("The CLI did not start.");
        _cliProcess.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppendLine(e.Data, ParseLogLevel(e.Data)); };
        _cliProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppendLine(e.Data, LogLevel.Error); };
        _cliProcess.BeginOutputReadLine();
        _cliProcess.BeginErrorReadLine();

        await _cliProcess.WaitForExitAsync(ct);
        LastExitCode = _cliProcess.ExitCode;
    }

    /// <summary>
    /// Unelevated window: starts the CLI through UAC. UAC cannot redirect output, so the run's
    /// session log is followed instead.
    /// </summary>
    private async Task RunCliElevatedAsync(RunMode mode, CancellationToken ct)
    {
        var cliPath = FindCliExecutable() ?? throw new FileNotFoundException("managedstatekeeper.exe was not found. Install StartSet or check the install path.");
        var before = SessionLogIndex.Snapshot();

        var startInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = string.Join(" ", RunModes.CliArguments(mode)),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        _cliProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("The CLI did not start.");
        AppendLine($"[INFO] {RunModes.Title(mode)} started as administrator (PID {_cliProcess.Id})", LogLevel.Info);

        var process = _cliProcess;
        var session = await WaitForSessionAsync(before, RunModes.SessionRunType(mode), TimeSpan.FromSeconds(15), ct, () => process.HasExited);
        if (session is not null)
            await TailSessionAsync(session, () => process.HasExited, ct);
        else
            AppendLine("[WARN] Could not find this run's session log; output is not shown. See the Logs tab.", LogLevel.Warning);

        await process.WaitForExitAsync(ct);
        LastExitCode = process.ExitCode;
    }

    // ── Stop ─────────────────────────────────────────────────────

    public void Stop()
    {
        if (!IsRunning) return;

        try
        {
            _cts?.Cancel();
            if (_cliProcess is { HasExited: false })
                _cliProcess.Kill(entireProcessTree: true);
        }
        catch { }
    }

    public void Clear()
    {
        OutputLines.Clear();
        LastExitCode = null;
        OnPropertyChanged(nameof(FilteredLines));
    }

    // ── Session log follower ───────────────────────────────────────

    private static async Task<SessionLogEntry?> WaitForSessionAsync(
        HashSet<string> before, string runType, TimeSpan timeout, CancellationToken ct, Func<bool>? giveUp = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (SessionLogIndex.FindNew(before, runType) is { } found)
                return found;
            if (giveUp?.Invoke() == true)
                return SessionLogIndex.FindNew(before, runType);
            await Task.Delay(500, ct);
        }
        return null;
    }

    /// <summary>Streams startset.log until <paramref name="finished"/> is true, then drains it.</summary>
    private async Task<string?> TailSessionAsync(SessionLogEntry session, Func<bool> finished, CancellationToken ct)
    {
        AppendLine($"[DEBUG] Following {session.LogPath}", LogLevel.Debug);

        long position = 0;
        var done = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var last = done;
            position = ReadNewLines(session.LogPath, position);
            if (last) break;
            done = finished();
            if (!done) await Task.Delay(300, ct);
        }

        return SessionLogIndex.ReadStatus(session);
    }

    private long ReadNewLines(string path, long position)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= position) return position;
            fs.Position = position;
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    AppendLine(line, ParseLogLevel(line));
            }
            return fs.Position;
        }
        catch (IOException) { return position; }
        catch (UnauthorizedAccessException) { return position; }
    }

    // ── Helpers ──────────────────────────────────────────────────

    private void AppendLine(string text, LogLevel level)
    {
        _dispatcher.TryEnqueue(() =>
        {
            OutputLines.Add(new OutputLine(text, level));
            OnPropertyChanged(nameof(FilteredLines));

            // "Executing <kind>: <script>" marks each payload as it starts.
            var executing = text.IndexOf("Executing ", StringComparison.Ordinal);
            if (executing >= 0)
            {
                PayloadCount++;
                var name = text[(executing + "Executing ".Length)..];
                var colon = name.IndexOf(": ", StringComparison.Ordinal);
                CurrentItemName = (colon >= 0 ? name[(colon + 2)..] : name).Trim();
            }

            if (level == LogLevel.Error)
                ErrorCount++;
        });
    }

    internal static LogLevel ParseLogLevel(string line)
    {
        if (HasLevel(line, "ERROR") || HasLevel(line, "ERR") || HasLevel(line, "FTL")) return LogLevel.Error;
        if (HasLevel(line, "WARN") || HasLevel(line, "WRN")) return LogLevel.Warning;
        if (HasLevel(line, "DEBUG") || HasLevel(line, "DBG")) return LogLevel.Debug;
        if (line.Contains("Execution complete", StringComparison.Ordinal)) return LogLevel.Success;
        return LogLevel.Info;
    }

    /// <summary>
    /// The session log writes "[timestamp] LEVEL message" and the console writes
    /// "[HH:mm:ss LVL] message"; this matches the level token in either.
    /// </summary>
    private static bool HasLevel(string line, string level)
    {
        if (line.StartsWith($"[{level}]", StringComparison.Ordinal)) return true;

        var close = line.IndexOf(']');
        if (close < 0) return false;

        var inside = line.AsSpan(1, Math.Max(0, close - 1));
        if (inside.EndsWith(" " + level, StringComparison.Ordinal)) return true;

        var rest = line.AsSpan(close + 1).TrimStart();
        return rest.StartsWith(level, StringComparison.Ordinal)
            && (rest.Length == level.Length || rest[level.Length] == ' ');
    }

    private static string? FindCliExecutable()
    {
        var candidates = new[]
        {
            Paths.ExecutablePath,
            Path.Combine(AppContext.BaseDirectory, "managedstatekeeper.exe"),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    partial void OnShowDebugChanged(bool value) => OnPropertyChanged(nameof(FilteredLines));
}
