using StartSet.Core.Enums;
using StartSet.Core.Models;

namespace StartSet.Engine.Interfaces;

/// <summary>
/// Interface for script/package processors.
/// </summary>
public interface IScriptProcessor
{
    /// <summary>
    /// Gets the file extensions this processor handles.
    /// </summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>
    /// Checks if this processor can handle the specified script.
    /// </summary>
    /// <param name="script">Script to check</param>
    /// <returns>True if this processor can handle it</returns>
    bool CanProcess(ScriptPayload script);

    /// <summary>
    /// Executes the script.
    /// </summary>
    /// <param name="script">Script to execute</param>
    /// <param name="timeout">Execution timeout</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Execution result</returns>
    Task<ExecutionResult> ExecuteAsync(
        ScriptPayload script,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
