using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Orion.Core.Diagnostics;

namespace Orion.Core.Auth;

public enum AccountType
{
    Offline = 0,
    Microsoft = 1,
}

/// <summary>
/// A launcher account.
///
/// Security notes:
/// * <see cref="AccessToken"/>/<see cref="RefreshToken"/> are only ever held in memory in
///   clear text; <see cref="AccountStore"/> encrypts them with Windows DPAPI (current user
///   scope) before they touch disk.
/// * <see cref="ToString"/> never prints tokens, so accidental logging is safe.
/// </summary>
public sealed class Account
{
    public required string Id { get; set; }
    public required string Username { get; set; }
    public required string Uuid { get; set; }
    public AccountType Type { get; set; } = AccountType.Offline;

    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }

    /// <summary>Xbox user hash (the <c>uhs</c> claim); required for token refresh chains.</summary>
    public string? XboxUserId { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public string? SkinModel { get; set; }

    public bool IsExpired => Type == AccountType.Microsoft
                             && (ExpiresAt is null || ExpiresAt.Value <= DateTimeOffset.UtcNow.AddMinutes(1));

    /// <summary>The Minecraft <c>--userType</c> value.</summary>
    public string UserType => Type == AccountType.Microsoft ? "msa" : "legacy";

    public string EffectiveUuid => string.IsNullOrWhiteSpace(Uuid)
        ? Util.OfflineUuid.FromPlayerName(Username).ToString("N")
        : Uuid;

    public override string ToString() => $"{Username} ({Type})";

    public Account Clone() => (Account)MemberwiseClone();
}

/// <summary>
/// Windows DPAPI wrapper (current-user scope) used to protect refresh tokens at rest.
/// No custom crypto is invented: the OS protects the blob with the user's logon credentials.
/// </summary>
public static class ProtectedData
{
    /// <summary>Marks an encrypted payload so plaintext can never be mistaken for ciphertext.</summary>
    private static readonly byte[] Magic = "ORION1"u8.ToArray();

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";

        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = ProtectBytes(data);
        var payload = new byte[Magic.Length + cipher.Length];
        Magic.CopyTo(payload, 0);
        cipher.CopyTo(payload, Magic.Length);
        return Convert.ToBase64String(payload);
    }

    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(protectedBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        if (payload.Length <= Magic.Length) return null;
        if (!payload.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;

        try
        {
            var data = UnprotectBytes(payload[Magic.Length..]);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or DllNotFoundException)
        {
            AppLog.Warn("security", "dpapi-unprotect-failed", ("error", ex.Message));
            return null;
        }
    }

    private static byte[] ProtectBytes(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) return data;

        var input = new DataBlob();
        var output = new DataBlob();
        try
        {
            input = DataBlob.FromBytes(data);
            if (!CryptProtectData(ref input, "Orion Launcher credentials", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                throw new CryptographicException(Marshal.GetLastWin32Error());

            return output.ToBytes();
        }
        finally
        {
            input.Free();
            output.Free();
        }
    }

    private static byte[] UnprotectBytes(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) return data;

        var input = new DataBlob();
        var output = new DataBlob();
        try
        {
            input = DataBlob.FromBytes(data);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                throw new CryptographicException(Marshal.GetLastWin32Error());

            return output.ToBytes();
        }
        finally
        {
            input.Free();
            output.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;

        public static DataBlob FromBytes(byte[] bytes)
        {
            var blob = new DataBlob
            {
                Size = bytes.Length,
                Data = Marshal.AllocHGlobal(bytes.Length),
            };
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            return blob;
        }

        public readonly byte[] ToBytes()
        {
            if (Size <= 0 || Data == IntPtr.Zero) return [];
            var buffer = new byte[Size];
            Marshal.Copy(Data, buffer, 0, Size);
            return buffer;
        }

        public void Free()
        {
            if (Data == IntPtr.Zero) return;
            Marshal.FreeHGlobal(Data);
            Data = IntPtr.Zero;
            Size = 0;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);
}
