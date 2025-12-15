using System.Security.Cryptography;
using System.Text;
using StartSet.Core.Constants;
using StartSet.Core.Models;
using StartSet.Infrastructure.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace StartSet.Infrastructure.Validation;

/// <summary>
/// Service for computing and validating script checksums.
/// </summary>
public class ChecksumService
{
    private static readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private ChecksumData _checksumData = new();
    private readonly string _checksumFilePath;

    /// <summary>
    /// Creates a new checksum service.
    /// </summary>
    /// <param name="checksumFilePath">Optional custom path for checksum file</param>
    public ChecksumService(string? checksumFilePath = null)
    {
        _checksumFilePath = checksumFilePath ?? Paths.ChecksumFile;
        LoadChecksums();
    }

    /// <summary>
    /// Computes SHA256 hash of a file.
    /// </summary>
    /// <param name="filePath">Path to the file</param>
    /// <returns>Hex-encoded SHA256 hash</returns>
    public static string ComputeChecksum(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Computes SHA256 hash of content.
    /// </summary>
    /// <param name="content">Content to hash</param>
    /// <returns>Hex-encoded SHA256 hash</returns>
    public static string ComputeChecksum(byte[] content)
    {
        var hashBytes = SHA256.HashData(content);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Validates a file against its stored checksum.
    /// </summary>
    /// <param name="filePath">Path to the file</param>
    /// <returns>True if checksum matches or no checksum exists</returns>
    public bool ValidateChecksum(string filePath)
    {
        if (!_checksumData.Checksums.TryGetValue(filePath, out var entry))
        {
            StartSetLogger.Debug("No checksum entry for {FilePath}, skipping validation", filePath);
            return true;
        }

        try
        {
            var currentChecksum = ComputeChecksum(filePath);
            var isValid = string.Equals(entry.Sha256, currentChecksum, StringComparison.OrdinalIgnoreCase);

            if (!isValid)
            {
                StartSetLogger.Warning("Checksum mismatch for {FilePath}: expected {Expected}, got {Actual}",
                    filePath, entry.Sha256, currentChecksum);
            }

            return isValid;
        }
        catch (Exception ex)
        {
            StartSetLogger.Warning("Failed to validate checksum for {FilePath}: {Error}", filePath, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Records a checksum for a file.
    /// </summary>
    /// <param name="filePath">Path to the file</param>
    /// <param name="comment">Optional comment</param>
    public void RecordChecksum(string filePath, string? comment = null)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var checksum = ComputeChecksum(filePath);

            _checksumData.Checksums[filePath] = new ChecksumEntry
            {
                Sha256 = checksum,
                Size = fileInfo.Length,
                RecordedAt = DateTimeOffset.UtcNow,
                Comment = comment
            };

            _checksumData.LastModified = DateTimeOffset.UtcNow;
            SaveChecksums();

            StartSetLogger.Debug("Recorded checksum for {FilePath}: {Checksum}", filePath, checksum);
        }
        catch (Exception ex)
        {
            StartSetLogger.Error(ex, "Failed to record checksum for {FilePath}", filePath);
            throw;
        }
    }

    /// <summary>
    /// Removes a checksum entry.
    /// </summary>
    public bool RemoveChecksum(string filePath)
    {
        if (_checksumData.Checksums.Remove(filePath))
        {
            _checksumData.LastModified = DateTimeOffset.UtcNow;
            SaveChecksums();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets all recorded checksums.
    /// </summary>
    public IReadOnlyDictionary<string, ChecksumEntry> GetAllChecksums() =>
        _checksumData.Checksums.AsReadOnly();

    /// <summary>
    /// Loads checksums from disk.
    /// </summary>
    private void LoadChecksums()
    {
        if (!File.Exists(_checksumFilePath))
        {
            _checksumData = new ChecksumData();
            return;
        }

        try
        {
            var yaml = File.ReadAllText(_checksumFilePath);
            _checksumData = _deserializer.Deserialize<ChecksumData>(yaml) ?? new ChecksumData();
        }
        catch (Exception ex)
        {
            StartSetLogger.Warning("Failed to load checksums from {Path}, starting fresh: {Error}", _checksumFilePath, ex.Message);
            _checksumData = new ChecksumData();
        }
    }

    /// <summary>
    /// Saves checksums to disk.
    /// </summary>
    private void SaveChecksums()
    {
        try
        {
            var directory = Path.GetDirectoryName(_checksumFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var yaml = _serializer.Serialize(_checksumData);
            File.WriteAllText(_checksumFilePath, yaml);
        }
        catch (Exception ex)
        {
            StartSetLogger.Error(ex, "Failed to save checksums to {Path}", _checksumFilePath);
        }
    }
}
