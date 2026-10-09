using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace StartSet.Infrastructure.Security;

/// <summary>Why a payload's signature did not verify.</summary>
public enum SignatureFailure
{
    None,

    /// <summary>The file type cannot carry an embedded signature line (executables, packages).</summary>
    UnsupportedType,

    /// <summary>The file has no signature line.</summary>
    NoSignature,

    /// <summary>The signature line is not 64 bytes of base64.</summary>
    MalformedSignature,

    /// <summary>The signature does not match the file and the key.</summary>
    BadSignature,

    /// <summary>The public key is not 32 bytes of base64.</summary>
    BadKey,

    /// <summary>The file could not be read.</summary>
    Unreadable
}

/// <summary>Outcome of checking one payload's signature.</summary>
public readonly record struct SignatureCheck(SignatureFailure Failure, string? Detail = null)
{
    public bool IsValid => Failure == SignatureFailure.None;

    public static SignatureCheck Valid => new(SignatureFailure.None);

    /// <summary>One line for the log and the items report.</summary>
    public string Describe() => Failure switch
    {
        SignatureFailure.None => "signature verified",
        SignatureFailure.UnsupportedType => "this file type cannot carry an embedded signature, and script signing is required",
        SignatureFailure.NoSignature => "no signature: the script carries no ed25519 signature line, and script signing is required",
        SignatureFailure.MalformedSignature => "bad signature: the ed25519 signature line is not a valid signature",
        SignatureFailure.BadSignature => "bad signature: the script does not match its ed25519 signature (modified after signing, or signed with another key)",
        SignatureFailure.BadKey => "bad key: the ManifestSigningKey policy value is not a base64 Ed25519 public key, so no payload can be verified",
        SignatureFailure.Unreadable => $"the file could not be read to verify its signature{(Detail is null ? "" : $": {Detail}")}",
        _ => Failure.ToString()
    };
}

/// <summary>
/// Ed25519 signatures embedded in script payloads, in the format outset uses on the Mac.
/// </summary>
/// <remarks>
/// <para>
/// A script carries its signature as one comment line: <c># ed25519: &lt;base64&gt;</c> in a
/// PowerShell script, <c>REM ed25519: &lt;base64&gt;</c> in a batch file. The signed bytes are
/// the file with that line removed, so the signature covers a stable payload that can be
/// read and versioned without it.
/// </para>
/// <para>
/// Canonical payload, exactly: the raw file bytes; a leading UTF-8 byte order mark is kept
/// but not part of the first line for prefix matching; the rest is split on LF; every line
/// that starts with the file type's prefix is dropped (a CR at the end of a line stays part
/// of that line); the remaining lines are joined with LF. For a PowerShell script with LF
/// line endings and no BOM this is byte for byte what outset signs, so one key and one
/// signing tool serve both platforms.
/// </para>
/// </remarks>
public static class ScriptSigning
{
    private const int SignatureSize = 64;
    private const int KeySize = 32;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>Signature line prefix for each script type that can carry one.</summary>
    private static readonly Dictionary<string, string> Prefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ps1"] = "# ed25519: ",
        [".cmd"] = "REM ed25519: ",
        [".bat"] = "REM ed25519: ",
    };

    /// <summary>The signature line prefix for <paramref name="path"/>'s type, or null when it cannot carry one.</summary>
    public static string? PrefixFor(string path) =>
        Prefixes.TryGetValue(Path.GetExtension(path), out var prefix) ? prefix : null;

    /// <summary>True when files of this type can carry an embedded signature.</summary>
    public static bool CanCarrySignature(string path) => PrefixFor(path) is not null;

    /// <summary>Decodes a base64 raw 32-byte Ed25519 public key; null when it is not one.</summary>
    public static Ed25519PublicKeyParameters? ParsePublicKey(string? base64)
    {
        var bytes = DecodeBase64(base64);
        return bytes is { Length: KeySize } ? new Ed25519PublicKeyParameters(bytes) : null;
    }

    /// <summary>Decodes a base64 raw 32-byte Ed25519 private key (the seed); null when it is not one.</summary>
    public static Ed25519PrivateKeyParameters? ParsePrivateKey(string? base64)
    {
        var bytes = DecodeBase64(base64);
        return bytes is { Length: KeySize } ? new Ed25519PrivateKeyParameters(bytes) : null;
    }

    /// <summary>A new keypair as base64 raw keys: (public, private).</summary>
    public static (string PublicKey, string PrivateKey) GenerateKeypair()
    {
        var privateKey = new Ed25519PrivateKeyParameters(new SecureRandom());
        return (Convert.ToBase64String(privateKey.GeneratePublicKey().GetEncoded()),
                Convert.ToBase64String(privateKey.GetEncoded()));
    }

    /// <summary>The bytes a signature covers: the file without its signature lines.</summary>
    public static byte[] CanonicalContent(byte[] content, string prefix)
    {
        var (bom, lines) = Split(content);
        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        return Join(bom, lines.Where(line => !StartsWith(line, prefixBytes)));
    }

    /// <summary>Signs a script file's content and returns the content with the signature line embedded.</summary>
    /// <exception cref="ArgumentException">The file type cannot carry a signature.</exception>
    public static byte[] Sign(byte[] content, string path, Ed25519PrivateKeyParameters privateKey)
    {
        var prefix = PrefixFor(path)
            ?? throw new ArgumentException($"{Path.GetExtension(path)} files cannot carry an embedded signature", nameof(path));

        var signature = SignBytes(CanonicalContent(content, prefix), privateKey);

        var (bom, lines) = Split(content);
        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        var kept = lines.Where(line => !StartsWith(line, prefixBytes)).ToList();

        // Match the file's line endings, judged by its first line, so signing a CRLF batch
        // file does not leave one LF-only line in it.
        var crlf = lines.Count > 1 && lines[0].Length > 0 && lines[0][^1] == (byte)'\r';
        var signatureLine = Encoding.UTF8.GetBytes(prefix + Convert.ToBase64String(signature) + (crlf ? "\r" : ""));

        // After a shebang, so the interpreter line stays first; otherwise at the top.
        var insertAt = kept.Count > 0 && StartsWith(kept[0], "#!"u8.ToArray()) ? 1 : 0;
        kept.Insert(insertAt, signatureLine);

        return Join(bom, kept);
    }

    /// <summary>Signs a file in place.</summary>
    public static void SignFile(string path, Ed25519PrivateKeyParameters privateKey) =>
        File.WriteAllBytes(path, Sign(File.ReadAllBytes(path), path, privateKey));

    /// <summary>Checks the signature embedded in <paramref name="content"/>.</summary>
    public static SignatureCheck Verify(byte[] content, string path, Ed25519PublicKeyParameters? publicKey)
    {
        if (publicKey is null)
            return new SignatureCheck(SignatureFailure.BadKey);

        var prefix = PrefixFor(path);
        if (prefix is null)
            return new SignatureCheck(SignatureFailure.UnsupportedType);

        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        var (_, lines) = Split(content);
        var signatureLine = lines.FirstOrDefault(line => StartsWith(line, prefixBytes));
        if (signatureLine is null)
            return new SignatureCheck(SignatureFailure.NoSignature);

        var encoded = Encoding.UTF8.GetString(signatureLine, prefixBytes.Length, signatureLine.Length - prefixBytes.Length).Trim();
        var signature = DecodeBase64(encoded);
        if (signature is not { Length: SignatureSize })
            return new SignatureCheck(SignatureFailure.MalformedSignature);

        var verifier = new Ed25519Signer();
        verifier.Init(false, publicKey);
        var payload = CanonicalContent(content, prefix);
        verifier.BlockUpdate(payload, 0, payload.Length);
        return verifier.VerifySignature(signature)
            ? SignatureCheck.Valid
            : new SignatureCheck(SignatureFailure.BadSignature);
    }

    /// <summary>Checks the signature embedded in the file at <paramref name="path"/>.</summary>
    public static SignatureCheck VerifyFile(string path, Ed25519PublicKeyParameters? publicKey)
    {
        if (publicKey is null)
            return new SignatureCheck(SignatureFailure.BadKey);
        if (!CanCarrySignature(path))
            return new SignatureCheck(SignatureFailure.UnsupportedType);

        byte[] content;
        try
        {
            content = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SignatureCheck(SignatureFailure.Unreadable, ex.Message);
        }

        return Verify(content, path, publicKey);
    }

    /// <summary>Raw Ed25519 signature over <paramref name="payload"/>.</summary>
    public static byte[] SignBytes(byte[] payload, Ed25519PrivateKeyParameters privateKey)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        signer.BlockUpdate(payload, 0, payload.Length);
        return signer.GenerateSignature();
    }

    private static (byte[] Bom, List<byte[]> Lines) Split(byte[] content)
    {
        var hasBom = content.AsSpan().StartsWith(Utf8Bom);
        var body = hasBom ? content.AsSpan(Utf8Bom.Length) : content.AsSpan();

        var lines = new List<byte[]>();
        while (true)
        {
            var newline = body.IndexOf((byte)'\n');
            if (newline < 0)
            {
                lines.Add(body.ToArray());
                break;
            }
            lines.Add(body[..newline].ToArray());
            body = body[(newline + 1)..];
        }

        return (hasBom ? Utf8Bom : [], lines);
    }

    private static byte[] Join(byte[] bom, IEnumerable<byte[]> lines)
    {
        using var stream = new MemoryStream();
        stream.Write(bom);
        var first = true;
        foreach (var line in lines)
        {
            if (!first) stream.WriteByte((byte)'\n');
            stream.Write(line);
            first = false;
        }
        return stream.ToArray();
    }

    private static bool StartsWith(byte[] line, byte[] prefix) => line.AsSpan().StartsWith(prefix);

    private static byte[]? DecodeBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
