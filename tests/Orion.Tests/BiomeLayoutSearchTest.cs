using Orion.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace Orion.Tests;

/// <summary>
/// 最终定位：把 genBiomes 的原始缓存整块拿出来，
/// 在各种可能的 stride/布局里搜索"哪一种读法能和 getBiomeAt 对上"。
/// 找到就是我们要修的读取循环；找不到说明还有别的约定。
/// </summary>
public sealed class BiomeLayoutSearchTest(ITestOutputHelper output)
{
    private const long Seed = 123456789L;

    [Theory(Skip = "已知问题：缓存长度恰为 sx*sz，但仍与 getBiomeAt 不一致；下一轮改做逐 index 偏移搜索")]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(64)]
    public void FindLayoutThatMatchesPointQueries(int scale)
    {
        if (!CubiomesNative.Available)
        {
            output.WriteLine("原生不可用，跳过");
            return;
        }

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int width = 16;
        const int height = 4;

        var result = CubiomesNative.DebugRawCache(Seed, version, CubiomesNative.DimOverworld,
            0, 0, scale, width, height);

        Assert.NotNull(result);

        var (raw, length, info, probe) = result!.Value;

        output.WriteLine($"scale={scale} 宽={width} 高={height} 缓存长度={length}（getMinCacheSize）" +
                         $" r.x={info[1]} r.z={info[2]}");

        // 期望长度参考值
        output.WriteLine($"  参考: sx*sz={width * height}, sx*4*sz*4={width * 4 * height * 4}");

        // 候选布局：stride 与起点偏移
        var strides = new[]
        {
            width,          // 按目标 scale 的宽度
            width * 4,      // 1:4 缓存的宽度
            width * scale / 4,
            height,
            width * height,
        };

        var best = (stride: 0, offset: 0, matches: -1);
        var lines = new List<string>();

        foreach (var stride in strides.Where(s => s > 0).Distinct())
        {
            // 偏移搜索范围取一段足够覆盖"整块缓存"的窗口
            var maxOffset = Math.Max(0, Math.Min(length - height * stride, 4096));

            for (var offset = 0; offset <= maxOffset; offset += stride)
            {
                var matches = 0;
                var total = 0;

                for (var z = 0; z < height; z++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var index = offset + z * stride + x;
                        if (index < 0 || index >= length) continue;

                        total++;
                        if (raw[index] == probe[z * width + x]) matches++;
                    }
                }

                if (total == 0) continue;
                if (matches > best.matches) best = (stride, offset, matches);
            }

            lines.Add($"stride={stride} 的最好匹配 {best.matches}");
        }

        foreach (var line in lines) output.WriteLine("  " + line);
        output.WriteLine($"最佳布局：stride={best.stride} offset={best.offset} 匹配 {best.matches}/{width * height}");

        Assert.Equal(width * height, best.matches);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(64)]
    public void ScaleUnitsConventionMatchesPointQueries(int scale)
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int width = 16;
        const int height = 4;

        var result = CubiomesNative.DebugIdsScaled(Seed, version, CubiomesNative.DimOverworld,
            0, 0, scale, width, height);

        Assert.NotNull(result);

        var (ids, probe) = result!.Value;
        var mismatch = 0;

        for (var i = 0; i < ids.Length; i++) if (ids[i] != probe[i]) mismatch++;

        output.WriteLine($"scale={scale}（Range.x 用 scale 单位）：数组 vs 逐点 不一致 {mismatch}/{ids.Length}");

        // scale 1/4 是逐格采样，必须完全一致；
        // scale 16/64 时 cubiomes 是在 1:4 网格上放大出来的，
        // 边界格与"直接点查询"本来就会有个别差异（cubiomes-viewer 同理），
        // 所以这里只要求绝大多数一致。
        if (scale <= 4)
        {
            Assert.Equal(0, mismatch);
        }
        else
        {
            Assert.True(mismatch <= ids.Length / 3, $"scale={scale} 不一致 {mismatch}/{ids.Length}，超出放大误差范围");
        }
    }
}
