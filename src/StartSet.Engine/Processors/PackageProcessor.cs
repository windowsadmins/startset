using System.Diagnostics;
using StartSet.Core.Enums;
using StartSet.Core.Models;
using StartSet.Engine.Interfaces;
using StartSet.Infrastructure.Logging;

namespace StartSet.Engine.Processors;

/// <summary>
/// Processor for Windows Installer packages (.msi, .msix).
/// </summary>
public class PackageProcessor : IScriptProcessor
{
    private static readonly string[] _extensions = [".msi", ".msix"];

    public IReadOnlyCollection<string> SupportedExtensions => _extensions;

    public bool CanProcess(ScriptPayload script) =>
        script.Extension is ".msi" or ".msix";

    public async Task<ExecutionResult> ExecuteAsync(
        ScriptPayload script,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return script.Extension switch
        {
            ".msi" => await ExecuteMsiAsync(script, timeout, cancellationToken),
            ".msix" => await ExecuteMsixAsync(script, timeout, cancellationToken),
            _ => ExecutionResult.Failed(script, -1, $"Unsupported package type: {script.Extension}")
        };
    }

    private async Task<ExecutionResult> ExecuteMsiAsync(
        ScriptPayload script,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = new ExecutionResult
        {
            Script = script,
            Status = ExecutionStatus.Failed,
            StartTime = DateTimeOffset.UtcNow
        };

        try
        {
            StartSetLogger.Information("Installing MSI package: {Script}", script.FileName);

            // Construct msiexec command with logging
            var logFile = Path.Combine(
                StartSet.Core.Constants.Paths.LogDirectory,
                $"{Path.GetFileNameWithoutExtension(script.FileName)}_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            var arguments = $"/i \"{script.FilePath}\" /qn /norestart /l*v \"{logFile}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };

            process.Start();
            var completed = await WaitForExitAsync(process, timeout, cancellationToken);

            result.EndTime = DateTimeOffset.UtcNow;

            if (!completed)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                result.Status = ExecutionStatus.Timeout;
                result.ErrorMessage = $"MSI installation timed out after {timeout.TotalSeconds:F0} seconds";
                StartSetLogger.Warning("MSI installation timed out: {Script}", script.FileName);
            }
            else
            {
                result.ExitCode = process.ExitCode;

                // MSI exit codes: 0 = success, 3010 = success (reboot required)
                result.Status = process.ExitCode is 0 or 3010
                    ? ExecutionStatus.Success
                    : ExecutionStatus.Failed;

                if (result.Status == ExecutionStatus.Failed)
                {
                    result.ErrorMessage = $"MSI exit code: {process.ExitCode} ({GetMsiErrorMessage(process.ExitCode)})";
                    StartSetLogger.Warning("MSI installation failed with exit code {ExitCode}: {Script}",
                        process.ExitCode, script.FileName);
                }
                else
                {
                    var message = process.ExitCode == 3010
                        ? "MSI package installed successfully (reboot required)"
                        : "MSI package installed successfully";
                    StartSetLogger.Information("{Message}: {Script}", message, script.FileName);
                }
            }

            result.StandardOutput = $"Log file: {logFile}";
        }
        catch (Exception ex)
        {
            result.EndTime = DateTimeOffset.UtcNow;
            result.Status = ExecutionStatus.Failed;
            result.ErrorMessage = ex.Message;
            result.Exception = ex;
            StartSetLogger.Error(ex, "MSI installation error: {Script}", script.FileName);
        }

        return result;
    }

    private async Task<ExecutionResult> ExecuteMsixAsync(
        ScriptPayload script,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = new ExecutionResult
        {
            Script = script,
            Status = ExecutionStatus.Failed,
            StartTime = DateTimeOffset.UtcNow
        };

        try
        {
            StartSetLogger.Information("Installing MSIX package: {Script}", script.FileName);

            // Use Add-AppxPackage PowerShell cmdlet
            var psCommand = $"Add-AppxPackage -Path '{script.FilePath}' -ForceApplicationShutdown";

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCommand}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
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
                result.ErrorMessage = $"MSIX installation timed out after {timeout.TotalSeconds:F0} seconds";
                StartSetLogger.Warning("MSIX installation timed out: {Script}", script.FileName);
            }
            else
            {
                result.ExitCode = process.ExitCode;
                result.Status = process.ExitCode == 0 ? ExecutionStatus.Success : ExecutionStatus.Failed;

                if (result.Status == ExecutionStatus.Failed)
                {
                    result.ErrorMessage = $"Exit code: {process.ExitCode}";
                    StartSetLogger.Warning("MSIX installation failed with exit code {ExitCode}: {Script}",
                        process.ExitCode, script.FileName);
                }
                else
                {
                    StartSetLogger.Information("MSIX package installed successfully: {Script}", script.FileName);
                }
            }
        }
        catch (Exception ex)
        {
            result.EndTime = DateTimeOffset.UtcNow;
            result.Status = ExecutionStatus.Failed;
            result.ErrorMessage = ex.Message;
            result.Exception = ex;
            StartSetLogger.Error(ex, "MSIX installation error: {Script}", script.FileName);
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

    private static string GetMsiErrorMessage(int exitCode) => exitCode switch
    {
        0 => "Success",
        13 => "Data is invalid",
        87 => "Invalid parameter",
        120 => "Function call not implemented",
        1259 => "Product not found",
        1601 => "Installation service not started",
        1602 => "User cancelled installation",
        1603 => "Fatal error during installation",
        1604 => "Installation suspended, incomplete",
        1605 => "This action is only valid for products that are currently installed",
        1618 => "Another installation is already in progress",
        1619 => "Installation package could not be opened",
        1620 => "Installation package path not found",
        1624 => "Error applying transforms",
        1625 => "Installation prohibited by policy",
        1638 => "Another version of this product is already installed",
        1639 => "Invalid command line argument",
        3010 => "Restart required",
        _ => $"Unknown error ({exitCode})"
    };
}
