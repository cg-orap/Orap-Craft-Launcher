using System.Runtime.InteropServices;
using System.Text;

namespace Orion.Core.Util;

/// <summary>
/// 用 Windows DPAPI（当前用户范围）加密需要落盘的小秘密，比如 CurseForge API Key。
///
/// 直接 P/Invoke <c>CryptProtectData</c>，不引入 NuGet 包：单文件发布几十 KB 的依赖没必要，
/// 而且这台机器的还原源不一定可达。密文只能在同一个 Windows 用户下解开，把 settings.json
/// 拷到别的机器或别的账户是解不出来的。
/// </summary>
public static class SecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>加密并转成 Base64；空值返回 null，非 Windows 或失败时退化为 null（调用方按明文处理）。</summary>
    public static string? Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var plain = Encoding.UTF8.GetBytes(secret);
            var protectedBytes = Transform(plain, encrypt: true);
            return protectedBytes is null ? null : Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解密；不是合法密文、换了用户或失败时返回 null。</summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return null;
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var raw = Convert.FromBase64String(protectedBase64);
            var plain = Transform(raw, encrypt: false);
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用于展示的掩码形式：只留前 7 位与后 4 位。</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return "";
        return secret.Length <= 12 ? new string('•', secret.Length) : secret[..7] + "…" + secret[^4..];
    }

    private static byte[]? Transform(byte[] input, bool encrypt)
    {
        var inputBlob = new DataBlob();
        var outputBlob = new DataBlob();

        try
        {
            inputBlob.Data = Marshal.AllocHGlobal(input.Length);
            inputBlob.Size = input.Length;
            Marshal.Copy(input, 0, inputBlob.Data, input.Length);

            var ok = encrypt
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob);

            if (!ok || outputBlob.Data == IntPtr.Zero || outputBlob.Size <= 0) return null;

            var result = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Size);
            return result;
        }
        finally
        {
            if (inputBlob.Data != IntPtr.Zero) Marshal.FreeHGlobal(inputBlob.Data);
            if (outputBlob.Data != IntPtr.Zero) LocalFree(outputBlob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
