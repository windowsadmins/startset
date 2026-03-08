using StartSet.Core.Enums;

namespace StartSet.Core.Models;

/// <summary>
/// Represents a script to be executed by StartSet.
/// </summary>
public class ScriptPayload
{
    /// <summary>
    /// Full path to the script file.
    /// </summary>
    public required string FilePath { get; set; }

    /// <summary>
    /// Script filename (without path).
    /// </summary>
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>
    /// File extension (lowercase, including dot).
    /// </summary>
    public string Extension => Path.GetExtension(FilePath).ToLowerInvariant();

    /// <summary>
    /// The payload type this script belongs to.
    /// </summary>
    public required PayloadType PayloadType { get; set; }

    /// <summary>
    /// SHA256 checksum of the script content.
    /// </summary>
    public string? Checksum { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long? FileSize { get; set; }

    /// <summary>
    /// Last modified timestamp of the file.
    /// </summary>
    public DateTimeOffset? LastModified { get; set; }

    /// <summary>
    /// Whether this script has already been executed (for run-once types).
    /// </summary>
    public bool AlreadyExecuted { get; set; }

    /// <summary>
    /// Whether this script should be skipped for any reason.
    /// </summary>
    public bool ShouldSkip { get; set; }

    /// <summary>
    /// Reason for skipping, if applicable.
    /// </summary>
    public string? SkipReason { get; set; }

    /// <summary>
    /// Sort order for execution (alphabetical by default).
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Returns true if this is a PowerShell script.
    /// </summary>
    public bool IsPowerShell => Extension == ".ps1";

    /// <summary>
    /// Returns true if this is a batch/cmd script.
    /// </summary>
    public bool IsBatch => Extension is ".cmd" or ".bat";

    /// <summary>
    /// Returns true if this is an executable.
    /// </summary>
    public bool IsExecutable => Extension == ".exe";

    /// <summary>
    /// Returns true if this is an MSI installer.
    /// </summary>
    public bool IsMsi => Extension == ".msi";

    /// <summary>
    /// Returns true if this is an MSIX package.
    /// </summary>
    public bool IsMsix => Extension == ".msix";

    /// <summary>
    /// Returns true if this is a package (MSI or MSIX).
    /// </summary>
    public bool IsPackage => IsMsi || IsMsix;
}
