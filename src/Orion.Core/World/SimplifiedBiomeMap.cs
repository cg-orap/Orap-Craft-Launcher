using System.Collections.Concurrent;

namespace Orion.Core.World;

/// <summary>简化地图里出现的群系（按原版大类划分，名称用中文）。</summary>
public enum BiomeKind
{
    DeepOcean,
    Ocean,
    River,
    Beach,
    Plains,
    Forest,
    DarkForest,
    Taiga,
    SnowyPlains,
    SnowyTaiga,
    Swamp,
    Jungle,
    Desert,
    Savanna,
    Badlands,
    Mountains,
    SnowyPeaks,
    Mushroom,
}

/// <summary>
/// 简化版生物群系地图。
///
/// **这不是原版算法**：原版 1.18+ 的群系由 noise router + density function + multi-noise 参数匹配
/// 决定（上万行），这里用 5 个独立噪声通道（大陆性、侵蚀、温度、湿度、怪异度）做**风格近似**，
/// 用来判断"往哪个方向走能遇到海/山/沙漠"。同一个种子 + 坐标永远得到同一个结果，可复现，
/// 但与游戏内实际地形**不保证一致**——界面上也明确这样标注。
/// </summary>
public static class SimplifiedBiomeMap
{
    private const double Scale = 1.0 / 1400.0;      // 大陆尺度
    private const double DetailScale = 1.0 / 260.0; // 局部细节

    private static readonly ConcurrentDictionary<long, ChannelSet> Cache = new();

    private sealed class ChannelSet
    {
        public required BiomeNoise Continental { get; init; }
        public required BiomeNoise Erosion { get; init; }
        public required BiomeNoise Temperature { get; init; }
        public required BiomeNoise Humidity { get; init; }
        public required BiomeNoise Weird { get; init; }
        public required BiomeNoise Detail { get; init; }
    }

    private static ChannelSet Channels(long seed) => Cache.GetOrAdd(seed, s => new ChannelSet
    {
        Continental = BiomeNoise.Create(s, 1),
        Erosion = BiomeNoise.Create(s, 2),
        Temperature = BiomeNoise.Create(s, 3),
        Humidity = BiomeNoise.Create(s, 4),
        Weird = BiomeNoise.Create(s, 5),
        Detail = BiomeNoise.Create(s, 6),
    });

    /// <summary>取某个方块坐标的群系。</summary>
    public static BiomeKind Sample(long seed, int blockX, int blockZ)
    {
        var channels = Channels(seed);

        var continental = channels.Continental.Fbm(blockX * Scale, blockZ * Scale, octaves: 4);
        var detail = channels.Detail.Fbm(blockX * DetailScale, blockZ * DetailScale, octaves: 3, gain: 0.55);
        var erosion = channels.Erosion.Fbm(blockX * Scale * 1.7, blockZ * Scale * 1.7, octaves: 3);
        var temperature = channels.Temperature.Fbm(blockX * Scale * 0.8, blockZ * Scale * 0.8, octaves: 2);
        var humidity = channels.Humidity.Fbm(blockX * Scale * 0.9, blockZ * Scale * 0.9, octaves: 2);
        var weird = channels.Weird.Fbm(blockX * Scale * 0.55, blockZ * Scale * 0.55, octaves: 2);

        // 稀有蘑菇岛
        if (weird > 0.86 && continental > 0.45) return BiomeKind.Mushroom;

        // 海洋
        var land = continental + detail * 0.28;
        if (land < -0.42) return BiomeKind.DeepOcean;
        if (land < -0.18) return BiomeKind.Ocean;

        // 河道：贴着海岸线、且怪异度接近 0 的细长带
        if (Math.Abs(weird) < 0.06 && land < 0.02) return BiomeKind.River;

        // 海滩
        if (land < 0.0) return BiomeKind.Beach;

        // 山地：侵蚀低 = 抬升
        if (erosion < -0.52 && continental > 0.15)
        {
            return temperature < -0.25 || erosion < -0.78 ? BiomeKind.SnowyPeaks : BiomeKind.Mountains;
        }

        // 剩下按温度/湿度分带（与游戏里"热→冷"的直觉一致）
        if (temperature < -0.42)
        {
            return humidity > 0.05 ? BiomeKind.SnowyTaiga : BiomeKind.SnowyPlains;
        }

        if (temperature > 0.5)
        {
            if (humidity < -0.45) return BiomeKind.Badlands;
            if (humidity < -0.12) return BiomeKind.Desert;
            if (humidity > 0.32) return BiomeKind.Jungle;
            return BiomeKind.Savanna;
        }

        if (humidity > 0.34) return land < 0.08 ? BiomeKind.Swamp : BiomeKind.DarkForest;
        if (humidity > 0.08) return temperature < -0.1 ? BiomeKind.Taiga : BiomeKind.Forest;
        return BiomeKind.Plains;
    }

    /// <summary>地图配色（尽量贴近游戏内地图色）。</summary>
    public static uint ColorOf(BiomeKind kind) => kind switch
    {
        BiomeKind.DeepOcean => 0xFF1B2E6E,
        BiomeKind.Ocean => 0xFF2C4A9A,
        BiomeKind.River => 0xFF3868C8,
        BiomeKind.Beach => 0xFFE2D6A2,
        BiomeKind.Plains => 0xFF8DB360,
        BiomeKind.Forest => 0xFF4E7A2E,
        BiomeKind.DarkForest => 0xFF2F5A1F,
        BiomeKind.Taiga => 0xFF6B8E5A,
        BiomeKind.SnowyPlains => 0xFFEDF1F5,
        BiomeKind.SnowyTaiga => 0xFFD3E3E8,
        BiomeKind.Swamp => 0xFF4C6B3C,
        BiomeKind.Jungle => 0xFF3F7D2B,
        BiomeKind.Desert => 0xFFD2B45A,
        BiomeKind.Savanna => 0xFFBFA55E,
        BiomeKind.Badlands => 0xFFB4601F,
        BiomeKind.Mountains => 0xFF9AA0A6,
        BiomeKind.SnowyPeaks => 0xFFC9CDD2,
        BiomeKind.Mushroom => 0xFF7C6A8A,
        _ => 0xFF000000,
    };

    public static string DisplayName(BiomeKind kind) => kind switch
    {
        BiomeKind.DeepOcean => "深海",
        BiomeKind.Ocean => "海洋",
        BiomeKind.River => "河流",
        BiomeKind.Beach => "沙滩",
        BiomeKind.Plains => "平原",
        BiomeKind.Forest => "森林",
        BiomeKind.DarkForest => "黑森林",
        BiomeKind.Taiga => "针叶林",
        BiomeKind.SnowyPlains => "雪原",
        BiomeKind.SnowyTaiga => "雪针叶林",
        BiomeKind.Swamp => "沼泽",
        BiomeKind.Jungle => "丛林",
        BiomeKind.Desert => "沙漠",
        BiomeKind.Savanna => "热带草原",
        BiomeKind.Badlands => "恶地",
        BiomeKind.Mountains => "山地",
        BiomeKind.SnowyPeaks => "雪山",
        BiomeKind.Mushroom => "蘑菇岛",
        _ => "未知",
    };

    /// <summary>
    /// 渲染一片区域的 RGBA 像素（可直接做 BitmapSource）。
    /// 采样步长 = <paramref name="blocksPerPixel"/> 个方块。
    /// </summary>
    public static byte[] RenderRgba(long seed, int centerBlockX, int centerBlockZ, int blocksPerPixel,
        int width, int height)
    {
        var step = Math.Max(1, blocksPerPixel);
        var left = centerBlockX - width / 2 * step;
        var top = centerBlockZ - height / 2 * step;
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = ColorOf(Sample(seed, left + x * step, top + y * step));
                var index = (y * width + x) * 4;
                pixels[index + 0] = (byte)((color >> 16) & 0xFF);
                pixels[index + 1] = (byte)((color >> 8) & 0xFF);
                pixels[index + 2] = (byte)(color & 0xFF);
                pixels[index + 3] = (byte)((color >> 24) & 0xFF);
            }
        }

        return pixels;
    }
}
