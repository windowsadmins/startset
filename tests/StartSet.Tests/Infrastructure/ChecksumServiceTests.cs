using FluentAssertions;
using StartSet.Infrastructure.Validation;
using StartSet.Tests.Helpers;

namespace StartSet.Tests.Infrastructure;

public class ChecksumServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ──────────────── ComputeChecksum (file) ────────────────

    [Fact]
    public void ComputeChecksum_File_ReturnsSha256Hex()
    {
        var filePath = _temp.CreateFile("test.txt", "hello world");

        var checksum = ChecksumService.ComputeChecksum(filePath);

        checksum.Should().NotBeNullOrEmpty();
        checksum.Should().HaveLength(64); // SHA256 = 32 bytes = 64 hex chars
        checksum.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ComputeChecksum_File_IsDeterministic()
    {
        var filePath = _temp.CreateFile("deterministic.txt", "same content");

        var hash1 = ChecksumService.ComputeChecksum(filePath);
        var hash2 = ChecksumService.ComputeChecksum(filePath);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ComputeChecksum_File_DifferentContentDifferentHash()
    {
        var file1 = _temp.CreateFile("a.txt", "content A");
        var file2 = _temp.CreateFile("b.txt", "content B");

        var hash1 = ChecksumService.ComputeChecksum(file1);
        var hash2 = ChecksumService.ComputeChecksum(file2);

        hash1.Should().NotBe(hash2);
    }

    // ──────────────── ComputeChecksum (bytes) ────────────────

    [Fact]
    public void ComputeChecksum_Bytes_ReturnsSha256Hex()
    {
        var hash = ChecksumService.ComputeChecksum("hello"u8.ToArray());

        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ComputeChecksum_EmptyBytes()
    {
        var hash = ChecksumService.ComputeChecksum(Array.Empty<byte>());

        // SHA256 of empty input is well-known
        hash.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    // ──────────────── ValidateChecksum ────────────────

    [Fact]
    public void ValidateChecksum_NoStoredChecksum_ReturnsTrue()
    {
        var svc = new ChecksumService(Path.Combine(_temp.Path, "checksums.yaml"));
        var filePath = _temp.CreateFile("norecord.txt", "anything");

        // No checksum recorded → validation passes (no entry means not tracked)
        svc.ValidateChecksum(filePath).Should().BeTrue();
    }

    [Fact]
    public void ValidateChecksum_AfterRecord_MatchingContent_ReturnsTrue()
    {
        var svc = new ChecksumService(Path.Combine(_temp.Path, "checksums.yaml"));
        var filePath = _temp.CreateFile("tracked.txt", "tracked content");

        svc.RecordChecksum(filePath);

        svc.ValidateChecksum(filePath).Should().BeTrue();
    }

    [Fact]
    public void ValidateChecksum_AfterModification_ReturnsFalse()
    {
        var svc = new ChecksumService(Path.Combine(_temp.Path, "checksums.yaml"));
        var filePath = _temp.CreateFile("modify.txt", "original content");

        svc.RecordChecksum(filePath);

        // Modify the file
        File.WriteAllText(filePath, "tampered content");

        svc.ValidateChecksum(filePath).Should().BeFalse();
    }

    // ──────────────── RecordChecksum ────────────────

    [Fact]
    public void RecordChecksum_PersistsToDisk()
    {
        var checksumFile = Path.Combine(_temp.Path, "persist.yaml");
        var filePath = _temp.CreateFile("persist.txt", "persistent");

        var svc1 = new ChecksumService(checksumFile);
        svc1.RecordChecksum(filePath);

        // Load fresh from disk
        var svc2 = new ChecksumService(checksumFile);
        svc2.ValidateChecksum(filePath).Should().BeTrue();
    }

    // ──────────────── RemoveChecksum ────────────────

    [Fact]
    public void RemoveChecksum_ExistingEntry_ReturnsTrue()
    {
        var svc = new ChecksumService(Path.Combine(_temp.Path, "checksums.yaml"));
        var filePath = _temp.CreateFile("remove.txt", "content");

        svc.RecordChecksum(filePath);
        svc.RemoveChecksum(filePath).Should().BeTrue();
    }

    [Fact]
    public void RemoveChecksum_NonExistent_ReturnsFalse()
    {
        var svc = new ChecksumService(Path.Combine(_temp.Path, "checksums.yaml"));
        svc.RemoveChecksum(@"C:\nonexistent\file.txt").Should().BeFalse();
    }
}
