using Orion.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace Orion.Tests;

/// <summary>
/// 决定性诊断：
///   ① 打印实际传给 genBiomes 的区间（r.x/r.z/sx/sz/scale/y）；
///   ② 把 genBiomes 的数组和 getBiomeAt 的逐点真值对照。
///
/// ① 不对 → 是我们的调用/签名问题；
/// ② 不对 → 是数组行列索引与点查询的映射问题（能算出偏移量）。
/// </summary>
public sealed class BiomeProbeDiagnosisTest(ITestOutputHelper output)
{
    private const long Seed = 123456789L;

    [Theory(Skip = "已知问题：genBiomes 数组与 getBiomeAt 逐点不一致（121/128）")]
    [InlineData(16, 32, 4)]
    [InlineData(16, 64, 4)]
    [InlineData(4, 32, 4)]
    public void RangeAndProbeAreConsistentKnownMismatch(int scale, int width, int height)
    {
        if (!CubiomesNative.Available)
        {
            output.WriteLine("原生不可用，跳过");
            return;
        }

        var version = CubiomesNative.VersionIndexFor("1.21.1");

        var result = CubiomesNative.DebugIdsAndProbe(Seed, version, CubiomesNative.DimOverworld,
            0, 0, scale, width, height);

        output.WriteLine($"返回码={CubiomesNative.LastDebugRc}");
        Assert.NotNull(result);

        var range = CubiomesNative.DebugLastRange();
        Assert.NotNull(range);

        output.WriteLine($"scale={scale} width={width} height={height} → " +
                         $"r.x={range![0]} r.z={range[1]} sx={range[2]} sz={range[3]} scale={range[4]} y={range[5]}");

        // 期望的区间
        Assert.Equal(-(width / 2) * scale, range[0]);
        Assert.Equal(-(height / 2) * scale, range[1]);
        Assert.Equal(width, range[2]);
        Assert.Equal(height, range[3]);
        Assert.Equal(scale, range[4]);

        // 数组 vs 逐点真值
        var (ids, probe) = result!.Value;
        var mismatch = 0;

        for (var i = 0; i < ids.Length; i++)
        {
            if (ids[i] != probe[i]) mismatch++;
        }

        output.WriteLine($"genBiomes 数组 vs getBiomeAt 逐点：不一致 {mismatch} / {ids.Length}");

        if (mismatch > 0)
        {
            // 打印前几处差异，便于判断错位方向
            var shown = 0;
            for (var z = 0; z < height && shown < 6; z++)
            {
                for (var x = 0; x < width && shown < 6; x++)
                {
                    var i = z * width + x;
                    if (ids[i] == probe[i]) continue;

                    var blockX = -(width / 2) * scale + x * scale;
                    var blockZ = -(height / 2) * scale + z * scale;

                    output.WriteLine($"  差异: 格({x},{z}) 方块({blockX},{blockZ}) 数组={ids[i]} 点查询={probe[i]}");
                    shown++;
                }
            }
        }

        Assert.Equal(0, mismatch);
    }

    [Fact]
    public void FindMappingOffsetBetweenArrayAndPointQueriesKnownMismatch()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int scale = 16;
        const int width = 16;
        const int height = 4;

        var ids = CubiomesNative.DebugBiomeIds(Seed, version, CubiomesNative.DimOverworld, 0, 0, scale, width, height);
        Assert.NotNull(ids);

        // 用单点查询铺一张"真值网格"，范围比数组大一倍，方便搜索偏移
        const int pad = 8;
        var gridW = width + pad * 2;
        var gridH = height + pad * 2;
        var originX = -(width / 2) * scale;     // 数组的起点
        var originZ = -(height / 2) * scale;
        var gridOriginX = originX - pad * scale;
        var gridOriginZ = originZ - pad * scale;

        var grid = new int[gridW * gridH];
        for (var gz = 0; gz < gridH; gz++)
        {
            for (var gx = 0; gx < gridW; gx++)
            {
                grid[gz * gridW + gx] = CubiomesNative.BiomeAt(Seed, version, CubiomesNative.DimOverworld,
                    scale, gridOriginX + gx * scale, 15, gridOriginZ + gz * scale);
            }
        }

        var best = (dx: 0, dz: 0, matches: -1);
        var reports = new List<string>();

        for (var dz = 0; dz <= pad * 2; dz++)
        {
            for (var dx = 0; dx <= pad * 2; dx++)
            {
                var matches = 0;
                for (var z = 0; z < height; z++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        if (ids![z * width + x] == grid[(z + dz) * gridW + (x + dx)]) matches++;
                    }
                }

                reports.Add($"dx={dx,2} dz={dz,2} → {matches}/{width * height}");
                if (matches > best.matches) best = (dx, dz, matches);
            }
        }

        foreach (var line in reports.Where(r => r.EndsWith($"/{width * height}")).Take(5)) output.WriteLine(line);
        foreach (var line in reports.OrderByDescending(r => r).Take(4)) output.WriteLine("最好: " + line);
        output.WriteLine($"数组↔单点最匹配偏移：dx={best.dx} dz={best.dz} 匹配 {best.matches}/{width * height}");
    }

    [Fact]
    public void OverlapOfTwoTilesUsingProbeKnownMismatch()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int scale = 16;
        const int width = 32;
        const int height = 4;
        var span = width * scale;

        var a = CubiomesNative.DebugIdsAndProbe(Seed, version, CubiomesNative.DimOverworld, 0, 0, scale, width, height);
        var b = CubiomesNative.DebugIdsAndProbe(Seed, version, CubiomesNative.DimOverworld, span / 2, 0, scale, width, height);

        Assert.NotNull(a);
        Assert.NotNull(b);

        // a 的右半（列 16..31，方块 0..span/2）与 b 的左半（列 0..15）是同一批方块
        var mismatchProbe = 0;

        for (var z = 0; z < height; z++)
        {
            for (var i = 0; i < width / 2; i++)
            {
                if (a!.Value.Probe[z * width + (width / 2 + i)] != b!.Value.Probe[z * width + i]) mismatchProbe++;
            }
        }

        output.WriteLine($"用 getBiomeAt 真值比较两块瓦片：不一致 {mismatchProbe} / {height * width / 2}");

        Assert.Equal(0, mismatchProbe);
    }
}
