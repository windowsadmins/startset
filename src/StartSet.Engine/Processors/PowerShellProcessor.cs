using System.Diagnostics;
using StartSet.Core.Enums;
using StartSet.Core.Models;
using StartSet.Engine.Interfaces;
using StartSet.Infrastructure.Logging;

namespace StartSet.Engine.Processors;

/// <summary>
/// Processor for PowerShell scripts (.ps1).
/// </summary>
public class PowerShellProcessor : IScriptProcessor
{
    private static readonly string[] _extensions = [".ps1"];

    public IReadOnlyCollection<string> SupportedExtensions => _extensions;

    public bool CanProcess(ScriptPayload script) =>
        script.Extension == ".ps1";

    public async Task<ExecutionResult> ExecuteAsync(
        ScriptPayload script,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var result = new ExecutionResult
        {
            Script = script,
            Status = ExecutionStatus.Failed,
            StartTime = DateTimeOffset.UtcNow
        };

        try
        {
            StartSetLogger.Information("Executing PowerShell script: {Script}", script.FileName);

            // Find PowerShell executable (prefer pwsh if available)
            var psPath = FindPowerShell();

            var startInfo = new ProcessStartInfo
            {
                FileName = psPath,
                Arguments = BuildArguments(script.FilePath),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script.FilePath) ?? Environment.CurrentDirectory
            };

            using var process = new Process { StartInfo = startInfo };
            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    outputBuilder.AppendLine(e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var completed = await WaitForExitAsync(process, timeout, cancellationToken);

            result.EndTime = DateTimeOffset.UtcNow;
            result.StandardOutput = outputBuilder.ToString().TrimEnd();
            result.StandardError = errorBuilder.ToString().TrimEnd();

            if (!completed)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                result.Status = ExecutionStatus.Timeout;
                result.ErrorMessage = $"Script execution timed out after {timeout.TotalSeconds:F0} seconds";
                StartSetLogger.Warning("PowerShell script timed out: {Script}", script.FileName);
            }
            else
            {
                result.ExitCode = process.ExitCode;
                result.Status = process.ExitCode == 0 ? ExecutionStatus.Success : ExecutionStatus.Failed;

                if (result.Status == ExecutionStatus.Failed)
                {
                    result.ErrorMessage = $"Exit code: {process.ExitCode}";
                    StartSetLogger.Warning("PowerShell script failed with exit code {ExitCode}: {Script}",
                        process.ExitCode, script.FileName);
                }
                else
                {
                    StartSetLogger.Information("PowerShell script completed successfully: {Script}", script.FileName);
                }
            }
        }
        catch (Exception ex)
        {
            result.EndTime = DateTimeOffset.UtcNow;
            result.Status = ExecutionStatus.Failed;
            result.ErrorMessage = ex.Message;
            result.Exception = ex;
            StartSetLogger.Error(ex, "PowerShell script execution error: {Script}", script.FileName);
        }

        return result;
    }

    private static string FindPowerShell()
    {
        // Prefer PowerShell 7+ (pwsh) if available
        var pwshLocations = new[]
        {
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            @"C:\Program Files (x86)\PowerShell\7\pwsh.exe",
            "pwsh.exe", // In PATH
        };

        foreach (var location in pwshLocations)
        {
            if (File.Exists(location))
                return location;

            // Check PATH
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = location,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                        return location;
                }
            }
            catch { }
        }

        // Fall back to Windows PowerShell
        return @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
    }

    private static string BuildArguments(string scriptPath)
    {
        // -NoProfile: Don't load profile (faster)
        // -NonInteractive: No prompts
        // -ExecutionPolicy Bypass: Allow script execution
        // -File: Execute the specified file
        return $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"";
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
