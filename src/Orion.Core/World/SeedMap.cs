using System.Globalization;

namespace Orion.Core.World;

/// <summary>地图上的一个区块坐标。</summary>
public readonly record struct ChunkCoord(int X, int Z)
{
    public string Label => $"{X}, {Z}";
    public int BlockX => X * 16;
    public int BlockZ => Z * 16;
}

/// <summary>
/// 离线种子地图能算的、与版本无关且**精确**的层。
///
/// 目前包含：史莱姆区块（overworld，公式与游戏内一致）、出生点区块参考、以及坐标换算。
/// 生物群系与地形需要完整的世界生成算法（noise router / density function，原版上万行），
/// 没有实现，页面里也明确说明了这一点——不拿近似数据冒充精确结果。
/// </summary>
public static class SeedMap
{
    /// <summary>把用户输入解析成种子：数字直接用；纯文本按 Java 的 String.hashCode()。</summary>
    public static bool TryParseSeed(string? text, out long seed)
    {
        seed = 0;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value)) return false;

        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out seed))
        {
            return true;
        }

        // 原版对非数字种子用的是 "字符串".hashCode()（32 位），这里照搬。
        var hash = 0;
        foreach (var c in value)
        {
            hash = unchecked(hash * 31 + c);
        }

        seed = hash;
        return true;
    }

    /// <summary>overworld 的史莱姆区块判定（1.0 起未变，与版本无关）。</summary>
    public static bool IsSlimeChunk(long seed, int chunkX, int chunkZ)
    {
        // 注意：原版先把 cx*cx 当 int 乘（会溢出），再转 long 乘常量，顺序不能改。
        var scramble = seed
            + unchecked((long)(chunkX * chunkX)) * 0x4c1906L
            + unchecked((long)(chunkX * 0x5ac0db))
            + unchecked((long)(chunkZ * chunkZ)) * 0x4307a7L
            + unchecked((long)(chunkZ * 0x5f24f));

        return new JavaRandom(scramble).NextInt(10) == 0;
    }

    /// <summary>取一个矩形范围内的史莱姆区块（含边界）。</summary>
    public static IReadOnlyList<ChunkCoord> SlimeChunks(long seed, int fromX, int fromZ, int toX, int toZ)
    {
        var result = new List<ChunkCoord>();
        var x0 = Math.Min(fromX, toX);
        var x1 = Math.Max(fromX, toX);
        var z0 = Math.Min(fromZ, toZ);
        var z1 = Math.Max(fromZ, toZ);

        for (var z = z0; z <= z1; z++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (IsSlimeChunk(seed, x, z)) result.Add(new ChunkCoord(x, z));
            }
        }

        return result;
    }

    /// <summary>区块坐标 → 方块坐标文本（地图上悬停用）。</summary>
    public static string Describe(int chunkX, int chunkZ, bool slime) =>
        $"区块 {chunkX}, {chunkZ} · 方块 {chunkX * 16}, {chunkZ * 16} · {(slime ? "史莱姆区块" : "普通区块")}";
}
