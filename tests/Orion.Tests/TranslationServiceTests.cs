using Orion.Core.Translation;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 翻译服务的分段逻辑。真正联网的翻译不放进单测（会不稳定），
/// 但"长描述怎么切段"是纯函数，必须测——切坏了会翻出半句话。
/// </summary>
public sealed class TranslationServiceTests
{
    [Fact]
    public void ShortTextStaysOneChunk()
    {
        var chunks = TranslationService.Split("A small mod that adds arrows.");

        Assert.Single(chunks);
        Assert.Equal("A small mod that adds arrows.", chunks[0]);
    }

    [Fact]
    public void BlankLineStartsANewChunk()
    {
        // 空行 = 段落分隔：分两段翻译，再拼回去，比塞成一大段更稳
        var chunks = TranslationService.Split("First paragraph.\n\nSecond paragraph.\n");

        Assert.Equal(2, chunks.Count);
        Assert.Equal("First paragraph.", chunks[0]);
        Assert.Equal("Second paragraph.", chunks[1]);
    }

    [Fact]
    public void LongTextIsSplitIntoChunksUnderTheLimit()
    {
        var text = string.Join("\n", Enumerable.Repeat("This is a fairly long line about the mod features and recipes.", 40));

        var chunks = TranslationService.Split(text);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 450, $"chunk too long: {chunk.Length}"));
        // 不丢内容
        Assert.Equal(40, string.Join(" ", chunks).Split("This is a fairly long line", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void SingleVeryLongLineIsSplitOnWordBoundaries()
    {
        var line = string.Join(" ", Enumerable.Repeat("word", 400));

        var chunks = TranslationService.Split(line);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.DoesNotContain("wordword", chunk));
    }
}
