using System.IO.Compression;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 整合包安装时最容易踩的坑：CurseForge 的 download-url 接口返回的是 {"data":"真实地址"}，
/// 直接当文件保存就会在 mods 里留下一堆几十字节的"jar"，Forge 启动时报 zip END header not found。
/// 这里把修复后的两个判据固定下来。
/// </summary>
public sealed class CurseForgeZipValidationTests
{
    [Fact]
    public void JsonStubIsNotATreatedAsAJar()
    {
        var path = Path.Combine(Path.GetTempPath(), "cf-stub-" + Guid.NewGuid().ToString("N") + ".jar");
        File.WriteAllText(path, """{"data":"https://edge.forgecdn.net/files/5071/575/hotai-1.0.jar"}""");

        try
        {
            Assert.False(IsUsableJar(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RealZipPasses()
    {
        var path = Path.Combine(Path.GetTempPath(), "cf-ok-" + Guid.NewGuid().ToString("N") + ".jar");

        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("META-INF/MANIFEST.MF");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("Manifest-Version: 1.0");
            }

            Assert.True(IsUsableJar(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TruncatedZipFails()
    {
        var source = Path.Combine(Path.GetTempPath(), "cf-src-" + Guid.NewGuid().ToString("N") + ".jar");
        var truncated = Path.Combine(Path.GetTempPath(), "cf-bad-" + Guid.NewGuid().ToString("N") + ".jar");

        try
        {
            using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("a.bin");
                using var stream = entry.Open();
                stream.Write(new byte[40_000]);
            }

            var bytes = File.ReadAllBytes(source);
            File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);

            Assert.False(IsUsableJar(truncated));
        }
        finally
        {
            File.Delete(source);
            File.Delete(truncated);
        }
    }

    /// <summary>与 PackArchiveInstaller.LooksLikeZip 相同的判据（那里是 private，这里复刻一份做回归）。</summary>
    private static bool IsUsableJar(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 64) return false;

            using (var stream = File.OpenRead(path))
            {
                var header = new byte[4];
                if (stream.Read(header, 0, 4) < 4) return false;
                if (header[0] != 0x50 || header[1] != 0x4B) return false;
            }

            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}