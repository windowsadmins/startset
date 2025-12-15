using System.Diagnostics;
using StartSet.Core.Enums;
using StartSet.Core.Models;
using StartSet.Engine.Interfaces;
using StartSet.Infrastructure.Logging;

namespace StartSet.Engine.Processors;

/// <summary>
/// Processor for batch/command files (.cmd, .bat).
/// </summary>
public class BatchProcessor : IScriptProcessor
{
    private static readonly string[] _extensions = [".cmd", ".bat"];

    public IReadOnlyCollection<string> SupportedExtensions => _extensions;

    public bool CanProcess(ScriptPayload script) =>
        script.Extension is ".cmd" or ".bat";

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
            StartSetLogger.Information("Executing batch script: {Script}", script.FileName);

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{script.FilePath}\"",
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
                StartSetLogger.Warning("Batch script timed out: {Script}", script.FileName);
            }
            else
            {
                result.ExitCode = process.ExitCode;
                result.Status = process.ExitCode == 0 ? ExecutionStatus.Success : ExecutionStatus.Failed;

                if (result.Status == ExecutionStatus.Failed)
                {
                    result.ErrorMessage = $"Exit code: {process.ExitCode}";
                    StartSetLogger.Warning("Batch script failed with exit code {ExitCode}: {Script}",
                        process.ExitCode, script.FileName);
                }
                else
                {
                    StartSetLogger.Information("Batch script completed successfully: {Script}", script.FileName);
                }
            }
        }
        catch (Exception ex)
        {
            result.EndTime = DateTimeOffset.UtcNow;
            result.Status = ExecutionStatus.Failed;
            result.ErrorMessage = ex.Message;
            result.Exception = ex;
            StartSetLogger.Error(ex, "Batch script execution error: {Script}", script.FileName);
        }

        return result;
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
