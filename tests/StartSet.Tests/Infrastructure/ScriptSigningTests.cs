using System.Text;
using FluentAssertions;
using StartSet.Infrastructure.Security;

namespace StartSet.Tests.Infrastructure;

/// <summary>
/// The embedded Ed25519 signature format. The known-answer tests pin the format to RFC 8032
/// test vectors, so any other implementation -- a CI signing step, outset on the Mac -- can
/// check itself against the same bytes.
/// </summary>
public class ScriptSigningTests
{
    // RFC 8032 section 7.1, TEST 1 (empty message) and TEST 2 (message 0x72).
    private const string Rfc8032Test1Secret = "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60";
    private const string Rfc8032Test1Public = "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a";
    private const string Rfc8032Test1Signature = "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b";
    private const string Rfc8032Test2Secret = "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb";
    private const string Rfc8032Test2Public = "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c";
    private const string Rfc8032Test2Signature = "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00";

    private static string B64(string hex) => Convert.ToBase64String(Convert.FromHexString(hex));

    private static readonly (string Public, string Private) Keys = ScriptSigning.GenerateKeypair();

    private static byte[] Sign(string content, string path) =>
        ScriptSigning.Sign(Encoding.UTF8.GetBytes(content), path, ScriptSigning.ParsePrivateKey(Keys.Private)!);

    private static SignatureCheck Verify(byte[] content, string path, string? key = null) =>
        ScriptSigning.Verify(content, path, ScriptSigning.ParsePublicKey(key ?? Keys.Public));

    // ──────────────── Known answers ────────────────

    [Fact]
    public void KnownAnswer_EmptyScript_SignsToRfc8032Test1()
    {
        var signed = ScriptSigning.Sign([], "empty.ps1", ScriptSigning.ParsePrivateKey(B64(Rfc8032Test1Secret))!);

        Encoding.UTF8.GetString(signed).Should().Be("# ed25519: " + B64(Rfc8032Test1Signature) + "\n");
        ScriptSigning.Verify(signed, "empty.ps1", ScriptSigning.ParsePublicKey(B64(Rfc8032Test1Public))).IsValid.Should().BeTrue();
    }

    [Fact]
    public void KnownAnswer_OneByteScript_SignsToRfc8032Test2()
    {
        // The canonical payload of "r" is the single byte 0x72: TEST 2's message.
        var signed = ScriptSigning.Sign("r"u8.ToArray(), "one.ps1", ScriptSigning.ParsePrivateKey(B64(Rfc8032Test2Secret))!);

        Encoding.UTF8.GetString(signed).Should().Be("# ed25519: " + B64(Rfc8032Test2Signature) + "\nr");
        ScriptSigning.Verify(signed, "one.ps1", ScriptSigning.ParsePublicKey(B64(Rfc8032Test2Public))).IsValid.Should().BeTrue();
    }

    [Fact]
    public void KnownAnswer_BatchPrefixCoversTheSameBytes()
    {
        var signed = ScriptSigning.Sign("r"u8.ToArray(), "one.cmd", ScriptSigning.ParsePrivateKey(B64(Rfc8032Test2Secret))!);

        Encoding.UTF8.GetString(signed).Should().Be("REM ed25519: " + B64(Rfc8032Test2Signature) + "\nr");
    }

    // ──────────────── Round trips ────────────────

    [Theory]
    [InlineData("script.ps1", "# ed25519: ")]
    [InlineData("script.cmd", "REM ed25519: ")]
    [InlineData("script.bat", "REM ed25519: ")]
    public void RoundTrip_EachScriptType(string path, string prefix)
    {
        var signed = Sign("Write-Output hi\nexit 0\n", path);

        Encoding.UTF8.GetString(signed).Should().StartWith(prefix);
        Verify(signed, path).IsValid.Should().BeTrue();
    }

    [Fact]
    public void RoundTrip_Crlf_KeepsCrlfAndVerifies()
    {
        var signed = Sign("@echo off\r\necho hi\r\n", "script.cmd");
        var text = Encoding.UTF8.GetString(signed);

        text.Should().MatchRegex("^REM ed25519: [A-Za-z0-9+/=]+\r\n@echo off\r\necho hi\r\n$");
        Verify(signed, "script.cmd").IsValid.Should().BeTrue();
    }

    [Fact]
    public void RoundTrip_Bom_StaysFirstAndVerifies()
    {
        var content = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("Write-Output hi\n"u8.ToArray()).ToArray();
        var signed = ScriptSigning.Sign(content, "script.ps1", ScriptSigning.ParsePrivateKey(Keys.Private)!);

        signed.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        Encoding.UTF8.GetString(signed, 3, signed.Length - 3).Should().StartWith("# ed25519: ");
        Verify(signed, "script.ps1").IsValid.Should().BeTrue();
    }

    [Fact]
    public void RoundTrip_Shebang_StaysFirstLine()
    {
        var signed = Encoding.UTF8.GetString(Sign("#!/usr/bin/env pwsh\nexit 0\n", "script.ps1"));

        signed.Split('\n')[0].Should().Be("#!/usr/bin/env pwsh");
        signed.Split('\n')[1].Should().StartWith("# ed25519: ");
    }

    [Fact]
    public void Resigning_ReplacesTheOldSignatureLine()
    {
        var once = Sign("exit 0\n", "script.ps1");
        var twice = ScriptSigning.Sign(once, "script.ps1", ScriptSigning.ParsePrivateKey(Keys.Private)!);

        Encoding.UTF8.GetString(twice).Split('\n').Count(l => l.StartsWith("# ed25519: ")).Should().Be(1);
        Verify(twice, "script.ps1").IsValid.Should().BeTrue();
    }

    // ──────────────── Failures ────────────────

    [Fact]
    public void Tampered_FailsAsBadSignature()
    {
        var signed = Encoding.UTF8.GetString(Sign("exit 0\n", "script.ps1")).Replace("exit 0", "exit 1");

        Verify(Encoding.UTF8.GetBytes(signed), "script.ps1").Failure.Should().Be(SignatureFailure.BadSignature);
    }

    [Fact]
    public void LineEndingChange_FailsAsBadSignature()
    {
        var signed = Encoding.UTF8.GetString(Sign("a\nb\n", "script.ps1")).Replace("a\nb", "a\r\nb");

        Verify(Encoding.UTF8.GetBytes(signed), "script.ps1").Failure.Should().Be(SignatureFailure.BadSignature);
    }

    [Fact]
    public void OtherKey_FailsAsBadSignature()
    {
        var signed = Sign("exit 0\n", "script.ps1");

        Verify(signed, "script.ps1", ScriptSigning.GenerateKeypair().PublicKey).Failure.Should().Be(SignatureFailure.BadSignature);
    }

    [Fact]
    public void Unsigned_FailsAsNoSignature() =>
        Verify("exit 0\n"u8.ToArray(), "script.ps1").Failure.Should().Be(SignatureFailure.NoSignature);

    [Fact]
    public void PowerShellPrefixInBatchFile_DoesNotCount()
    {
        var signed = Sign("exit 0\n", "script.ps1");

        Verify(signed, "script.cmd").Failure.Should().Be(SignatureFailure.NoSignature);
    }

    [Fact]
    public void GarbageSignature_FailsAsMalformed() =>
        Verify("# ed25519: not-base64!\nexit 0\n"u8.ToArray(), "script.ps1").Failure.Should().Be(SignatureFailure.MalformedSignature);

    [Theory]
    [InlineData("tool.exe")]
    [InlineData("app.msi")]
    [InlineData("app.msix")]
    public void BinaryPayloads_CannotCarryASignature(string path) =>
        Verify([1, 2, 3], path).Failure.Should().Be(SignatureFailure.UnsupportedType);

    [Theory]
    [InlineData("not base64")]
    [InlineData("AAAA")]
    public void BadPublicKey_FailsAsBadKey(string key) =>
        ScriptSigning.Verify(Sign("exit 0\n", "script.ps1"), "script.ps1", ScriptSigning.ParsePublicKey(key))
            .Failure.Should().Be(SignatureFailure.BadKey);
}
