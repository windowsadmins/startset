using System.Diagnostics;
using StartSet.Core.Enums;
using StartSet.Core.Models;
using StartSet.Engine.Interfaces;
using StartSet.Engine.Native;
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
            var scriptArguments = BuildArguments(script.FilePath);
            var scriptWorkingDirectory = Path.GetDirectoryName(script.FilePath) ?? Environment.CurrentDirectory;

            // login-* and on-demand payloads are documented as user context. The
            // service runs as LocalSystem, so without this they execute as SYSTEM
            // in session 0: HKCU writes land in SYSTEM's hive, user32 calls never
            // reach the desktop, and scripts that skip administrators match SYSTEM
            // and exit 0 while appearing to succeed.
            if (RequiresUserContext(script.PayloadType))
            {
                var asUser = UserSessionLauncher.Run(psPath, scriptArguments, scriptWorkingDirectory, timeout);

                if (asUser.Launched)
                {
                    result.EndTime = DateTimeOffset.UtcNow;
                    result.StandardOutput = asUser.StandardOutput;
                    result.StandardError = asUser.StandardError;

                    if (asUser.TimedOut)
                    {
                        result.Status = ExecutionStatus.Timeout;
                        result.ErrorMessage = $"Script execution timed out after {timeout.TotalSeconds:F0} seconds";
                        StartSetLogger.Warning("PowerShell script timed out in user session: {Script}", script.FileName);
                    }
                    else
                    {
                        result.ExitCode = asUser.ExitCode;
                        result.Status = asUser.ExitCode == 0 ? ExecutionStatus.Success : ExecutionStatus.Failed;
                        if (result.Status == ExecutionStatus.Failed)
                        {
                            result.ErrorMessage = $"Exit code: {asUser.ExitCode}";
                            StartSetLogger.Warning("PowerShell script failed in user session with exit code {ExitCode}: {Script}",
                                asUser.ExitCode, script.FileName);
                        }
                        else
                        {
                            StartSetLogger.Information("PowerShell script completed successfully in user session: {Script}", script.FileName);
                        }
                    }

                    return result;
                }

                // Fall through to SYSTEM execution rather than dropping the script.
                // Logged loudly because a user-context payload running as SYSTEM is
                // the failure this code exists to prevent -- silence here would
                // recreate the original bug invisibly.
                StartSetLogger.Warning(
                    "Could not run {Script} as the console user ({Reason}); falling back to SYSTEM context. Per-user settings in this script will not apply.",
                    script.FileName, asUser.FailureReason ?? "unknown");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = psPath,
                Arguments = scriptArguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = scriptWorkingDirectory
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

    /// <summary>
    /// Payload types whose scripts are documented to run as the signed-in user.
    /// The *-privileged and boot-* types deliberately stay in the service's own
    /// SYSTEM context.
    /// </summary>
    private static bool RequiresUserContext(PayloadType type) => type switch
    {
        PayloadType.LoginOnce => true,
        PayloadType.LoginEvery => true,
        PayloadType.OnDemand => true,
        _ => false
    };

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
