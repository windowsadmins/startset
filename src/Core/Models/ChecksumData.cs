using YamlDotNet.Serialization;

namespace StartSet.Core.Models;

/// <summary>
/// Checksum validation data for scripts.
/// </summary>
public class ChecksumData
{
    /// <summary>
    /// Version of the checksum data format.
    /// </summary>
    [YamlMember(Alias = "version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Last modification timestamp.
    /// </summary>
    [YamlMember(Alias = "last_modified")]
    public DateTimeOffset LastModified { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Dictionary of script path to checksum entry.
    /// </summary>
    [YamlMember(Alias = "checksums")]
    public Dictionary<string, ChecksumEntry> Checksums { get; set; } = new();
}

/// <summary>
/// Individual checksum entry for a script.
/// </summary>
public class ChecksumEntry
{
    /// <summary>
    /// SHA256 hash of the script content.
    /// </summary>
    [YamlMember(Alias = "sha256")]
    public required string Sha256 { get; set; }

    /// <summary>
    /// File size in bytes when checksum was calculated.
    /// </summary>
    [YamlMember(Alias = "size")]
    public long Size { get; set; }

    /// <summary>
    /// Timestamp when checksum was recorded.
    /// </summary>
    [YamlMember(Alias = "recorded_at")]
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional comment/description.
    /// </summary>
    [YamlMember(Alias = "comment")]
    public string? Comment { get; set; }
}
