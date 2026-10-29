using System.Text.Json;
using Orion.Core.Settings;
using Orion.Core.Util;
using Xunit;

namespace Orion.Tests;

/// <summary>CurseForge API Key 的落盘保护：不能以明文写进 settings.json。</summary>
public sealed class SecretProtectorTests
{
    private const string SampleKey = "$2a$10$abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUV0123456789";

    [Fact]
    public void RoundTripsOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var cipher = SecretProtector.Protect(SampleKey);
        Assert.NotNull(cipher);
        Assert.NotEqual(SampleKey, cipher);
        Assert.Equal(SampleKey, SecretProtector.Unprotect(cipher));
    }

    [Fact]
    public void CiphertextIsBlobAndSurvivesBase64()
    {
        if (!OperatingSystem.IsWindows()) return;

        var cipher = SecretProtector.Protect(SampleKey)!;
        var bytes = Convert.FromBase64String(cipher);          // 必须是合法 Base64
        Assert.True(bytes.Length > SampleKey.Length / 2);      // DPAPI 输出是二进制块
        Assert.DoesNotContain("abcdefgh", cipher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GarbageDecryptsToNullInsteadOfThrowing()
    {
        Assert.Null(SecretProtector.Unprotect(null));
        Assert.Null(SecretProtector.Unprotect(""));
        Assert.Null(SecretProtector.Unprotect("not-base64!!"));
        Assert.Null(SecretProtector.Unprotect(Convert.ToBase64String([1, 2, 3, 4])));
    }

    [Fact]
    public void PlaintextKeyNeverAppearsInSerializedSettings()
    {
        var settings = new LauncherSettings { CurseForgeApiKey = SampleKey };
        var json = JsonSerializer.Serialize(settings);

        // 明文 Key 不会进 JSON；旧字段只留一个 null 占位
        Assert.DoesNotContain(SampleKey, json);
        Assert.Contains("\"curseForgeApiKey\":null", json);
    }

    [Fact]
    public void PlaintextFileIsMigratedOnLoad()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "orap-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new Orion.Core.Game.GamePaths(root);
            File.WriteAllText(paths.SettingsFile,
                "{\n  \"curseForgeApiKey\": \"" + SampleKey + "\"\n}");

            var store = new Orion.Core.Settings.SettingsStore(paths);
            var settings = store.Load();

            // 内存里拿到明文，磁盘上只剩密文
            Assert.Equal(SampleKey, settings.CurseForgeApiKey);

            var onDisk = File.ReadAllText(paths.SettingsFile);
            Assert.DoesNotContain(SampleKey, onDisk);
            Assert.Contains("curseForgeKeyProtected", onDisk);
            Assert.Equal(SampleKey, SecretProtector.Unprotect(settings.CurseForgeApiKeyProtected));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void MaskKeepsOnlyEdges()
    {
        var masked = SecretProtector.Mask(SampleKey);
        Assert.Contains("…", masked);
        Assert.DoesNotContain(SampleKey, masked);
        Assert.Equal("", SecretProtector.Mask(""));
    }
}