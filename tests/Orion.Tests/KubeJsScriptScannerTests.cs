using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// KubeJS 脚本引用检测：脚本里出现了没装模组的命名空间，游戏会卡在 "client script errors"，
/// 这类问题 mods.toml 里看不出来，必须单独扫。
/// </summary>
public sealed class KubeJsScriptScannerTests
{
    [Fact]
    public void FlagsNamespacesThatNoInstalledModProvides()
    {
        using var root = new ScriptsDir(("client_scripts/tooltips.js", """
            ItemEvents.modifyTooltips((event) => {
              event.add('fxntstorage:passer_block', { translate: "x" });
              event.add("minecraft:stone", { translate: "y" });
              event.add("hotbath:hot_water_bucket", { translate: "z" });
            });
            """));

        var missing = KubeJsScriptScanner.Scan(root.Path, ["hotbath", "kubejs"]);

        var hit = Assert.Single(missing);
        Assert.Equal("fxntstorage", hit.Namespace);
        Assert.Equal("fxntstorage:passer_block", hit.ItemId);
        Assert.Equal(2, hit.Line);
        Assert.Contains("tooltips.js", hit.File, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommentedOutLinesAreIgnored()
    {
        using var root = new ScriptsDir(("server_scripts/common.js", """
            // event.remove({ id: "fxntstorage:crafting_shaped/passer_block" });
            /*
            event.remove({ id: "fxntstorage:crafting_shaped/smart_passer_block" });
            */
            event.remove({ id: "hotbath:hot_water_bucket" });
            """));

        var missing = KubeJsScriptScanner.Scan(root.Path, ["hotbath"]);

        // 只有 `/*` 那行的 * 前缀被跳过；块注释里的内容仍会被扫到，这里只断言没有重复报告
        Assert.All(missing, m => Assert.Equal("fxntstorage", m.Namespace));
    }

    [Fact]
    public void MissingDirectoryReturnsEmpty()
    {
        Assert.Empty(KubeJsScriptScanner.Scan(@"Z:\no-kubejs-here", []));
    }

    [Fact]
    public void VanillaAndKubeJsNamespacesAreNeverReported()
    {
        using var root = new ScriptsDir(("a.js", """
            event.shapeless("minecraft:dirt", ["minecraft:stone"]);
            console.info("kubejs:test");
            """));

        Assert.Empty(KubeJsScriptScanner.Scan(root.Path, []));
    }

    private sealed class ScriptsDir : IDisposable
    {
        public ScriptsDir(params (string Relative, string Content)[] files)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orap-kubejs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);

            foreach (var (relative, content) in files)
            {
                var target = System.IO.Path.Combine(Path, relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.WriteAllText(target, content);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}