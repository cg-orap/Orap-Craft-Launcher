using Orion.Core.Game;
using Xunit;

namespace Orion.Tests;

public sealed class GameOptionsTemplateTests
{
    [Fact]
    public void MergeKeepsUnrelatedKeysAndComments()
    {
        const string existing = """
            #Options
            version:3465
            renderDistance:4
            key_key.jump:space
            guiScale:2
            """;

        var merged = GameOptionsTemplate.Merge(existing, new GameOptionsTemplate
        {
            RenderDistance = 16,
            GuiScale = 0,
            AutoJump = true,
        }.ToKeyValues());

        Assert.Contains("renderDistance:16", merged);
        Assert.Contains("guiScale:0", merged);
        Assert.Contains("autoJump:true", merged);
        Assert.Contains("key_key.jump:space", merged);   // 按键绑定不能丢
        Assert.Contains("#Options", merged);
        Assert.DoesNotContain("renderDistance:4", merged);
        Assert.Equal(1, merged.Split("renderDistance").Length - 1);   // 不会写出重复键
    }

    [Fact]
    public void MergeAppendsMissingKeysOnce()
    {
        var merged = GameOptionsTemplate.Merge("version:3465\n", new GameOptionsTemplate().ToKeyValues());
        Assert.Contains("fov:70", merged);
        Assert.Contains("gamma:0.5", merged);
        Assert.EndsWith("\n", merged);
        Assert.Equal(1, merged.Split("fov:").Length - 1);
    }

    [Fact]
    public void ValuesAreClampedToSaneRanges()
    {
        var values = new GameOptionsTemplate
        {
            RenderDistance = 999,
            GuiScale = 9,
            Fov = 5,
            Gamma = 3,
            Particles = "nonsense",
        }.ToKeyValues();

        Assert.Equal("32", values["renderDistance"]);
        Assert.Equal("4", values["guiScale"]);
        Assert.Equal("30", values["fov"]);
        Assert.Equal("1.0", values["gamma"]);
        Assert.Equal("all", values["particles"]);
    }

    [Fact]
    public void ApplyWritesTheFileAndRespectsTheEnabledSwitch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orion-options-" + Guid.NewGuid().ToString("N"));
        try
        {
            var template = new GameOptionsTemplate { RenderDistance = 20, AutoJump = true };
            Assert.Equal(7, template.Apply(dir));
            var written = File.ReadAllText(Path.Combine(dir, "options.txt"));
            Assert.Contains("renderDistance:20", written);
            Assert.Contains("autoJump:true", written);

            // 再次应用是幂等的（合并而不是追加重复键）
            template.Apply(dir);
            Assert.Equal(1, File.ReadAllText(Path.Combine(dir, "options.txt")).Split("renderDistance:").Length - 1);

            var off = new GameOptionsTemplate { Enabled = false };
            var other = Path.Combine(dir, "off");
            Assert.Equal(0, off.Apply(other));
            Assert.False(File.Exists(Path.Combine(other, "options.txt")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}