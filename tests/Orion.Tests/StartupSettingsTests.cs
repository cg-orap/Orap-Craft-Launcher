using Orion.Core;
using Orion.Core.Util;
using Orion.Core.Settings;
using Xunit;

namespace Orion.Tests;

/// <summary>启动路径：配置再怪也不能把启动器卡死，Key 必须是加密落盘。</summary>
public sealed class StartupSettingsTests
{
    private const string LegacyKey = "$2a$10$legacyKeyValueForTest0000000000000000000000";

    [Fact]
    public void CreateWithLegacyPlaintextKeyDoesNotThrowAndMigrates()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var root = new TempRoot();
        File.WriteAllText(root.SettingsFile,
            "{\n  \"curseForgeApiKey\": \"" + LegacyKey + "\",\n  \"theme\": \"Snow\"\n}");

        var context = LauncherContext.Create(root.Path);

        Assert.Equal(LegacyKey, context.Settings.CurseForgeApiKey);
        Assert.Equal("Snow", context.Settings.Theme);

        var onDisk = File.ReadAllText(root.SettingsFile);
        Assert.DoesNotContain("legacyKeyValueForTest", onDisk);
        Assert.Contains("curseForgeKeyProtected", onDisk);
    }

    [Fact]
    public void CorruptKeyFieldDoesNotBreakStartup()
    {
        using var root = new TempRoot();

        // 密文是垃圾、明文也没有：解不开时应当当作"没配 Key"，而不是抛异常
        File.WriteAllText(root.SettingsFile,
            "{\n  \"curseForgeKeyProtected\": \"这不是合法密文\",\n  \"theme\": \"Paper\"\n}");

        var context = LauncherContext.Create(root.Path);

        Assert.Equal("Paper", context.Settings.Theme);
        Assert.Null(context.Settings.CurseForgeApiKey);
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        using var root = new TempRoot();
        File.WriteAllText(root.SettingsFile,
            "{\n  \"theme\": \"Rock\",\n  \"somethingFromTheFuture\": { \"a\": 1 }\n}");

        var context = LauncherContext.Create(root.Path);
        Assert.Equal("Rock", context.Settings.Theme);
    }

    [Fact]
    public void RealSettingsFileOnThisMachineIsReadable()
    {
        // 只在测试机存在真实数据目录时检查（不依赖它一定存在）
        const string real = @"D:\gd\orion\settings.json";
        if (!File.Exists(real)) return;

        var json = File.ReadAllText(real);
        var settings = JsonUtil.Deserialize<LauncherSettings>(json);
        Assert.NotNull(settings);
        Assert.False(string.IsNullOrWhiteSpace(settings!.Theme));
    }

    [Fact]
    public void StartupWorksWithTheRealSettingsFile()
    {
        const string real = @"D:\gd\orion\settings.json";
        if (!File.Exists(real)) return;

        using var root = new TempRoot();
        File.Copy(real, root.SettingsFile);

        // 这台机器上真实配置的完整内容必须能走完启动流程
        var context = LauncherContext.Create(root.Path);
        Assert.NotNull(context.Settings);

        var onDisk = File.ReadAllText(root.SettingsFile);
        Assert.DoesNotContain("$2a$10$aEMjaoWJ", onDisk);
        Assert.Contains("curseForgeKeyProtected", onDisk);
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orap-startup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string SettingsFile => System.IO.Path.Combine(Path, "settings.json");

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}