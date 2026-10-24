using System.Security.Cryptography;

namespace Orion.Core.Util;

public enum HashAlgorithmKind
{
    Sha1,
    Sha256,
    Sha512,
}

/// <summary>Streaming hash helpers used for download verification and integrity repair.</summary>
public static class FileHashing
{
    public const int DefaultBufferSize = 128 * 1024;

    public static string AlgorithmName(HashAlgorithmKind kind) => kind switch
    {
        HashAlgorithmKind.Sha1 => "SHA1",
        HashAlgorithmKind.Sha256 => "SHA256",
        HashAlgorithmKind.Sha512 => "SHA512",
        _ => "SHA1",
    };

    public static async Task<string> HashFileAsync(string path, HashAlgorithmKind kind, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultBufferSize, useAsync: true);
        using var algorithm = Create(kind);
        var hash = await algorithm.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static string HashString(string value, HashAlgorithmKind kind)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        using var algorithm = Create(kind);
        return Convert.ToHexStringLower(algorithm.ComputeHash(bytes));
    }

    /// <summary>
    /// Verifies a file against an expected hex digest. Returns <c>true</c> when
    /// <paramref name="expected"/> is null/empty (nothing to check) — callers decide
    /// whether an absent digest is acceptable.
    /// </summary>
    public static async Task<bool> VerifyAsync(string path, string? expected, HashAlgorithmKind kind = HashAlgorithmKind.Sha1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        if (!File.Exists(path)) return false;

        try
        {
            var actual = await HashFileAsync(path, kind, ct).ConfigureAwait(false);
            return string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static HashAlgorithm Create(HashAlgorithmKind kind) => kind switch
    {
        // SHA-1 is mandated by the Minecraft launcher metadata format, so it is used for
        // integrity checking only (never for signatures or password storage).
#pragma warning disable CA5350 // Do Not Use Weak Cryptographic Algorithms
        HashAlgorithmKind.Sha1 => SHA1.Create(),
#pragma warning restore CA5350
        HashAlgorithmKind.Sha256 => SHA256.Create(),
        HashAlgorithmKind.Sha512 => SHA512.Create(),
        _ => SHA1.Create(),
    };
}

/// <summary>
/// Deterministic offline player UUIDs. This reproduces the well known
/// "OfflinePlayer:&lt;name&gt;" name-based (v3) UUID that vanilla servers compute for
/// online-mode=false, so single player / LAN saves keep the same identity.
/// </summary>
public static class OfflineUuid
{
    public static Guid FromPlayerName(string name)
    {
        // MD5 is what the vanilla server uses for offline UUIDs; this is an identifier,
        // not a security primitive.
#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms
        var hash = MD5.HashData(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
#pragma warning restore CA5351

        // Version 3 (name based) and the RFC 4122 variant, at their canonical byte positions.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        // Java's UUID.nameUUIDFromBytes produces the canonical (big-endian) field layout, while
        // System.Guid stores its first three fields little-endian. The bytes are therefore
        // reordered so that Guid.ToString() prints exactly the UUID a vanilla server computes
        // for "OfflinePlayer:<name>".
        Span<byte> bytes = stackalloc byte[16];
        bytes[0] = hash[3];
        bytes[1] = hash[2];
        bytes[2] = hash[1];
        bytes[3] = hash[0];
        bytes[4] = hash[5];
        bytes[5] = hash[4];
        bytes[6] = hash[7];
        bytes[7] = hash[6];
        hash.AsSpan(8, 8).CopyTo(bytes[8..]);

        return new Guid(bytes);
    }
}
