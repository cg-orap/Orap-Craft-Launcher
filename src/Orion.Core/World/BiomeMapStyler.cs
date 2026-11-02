using System.Collections.Concurrent;

namespace Orion.Core.World;

/// <summary>
/// 把 cubiomes 出来的"纯色群系图"加工成有质感的成品图。
///
/// 原生渲染每像素只给一个纯色（每个群系一个固定色），所以整张图看起来像色块拼图。
/// 这里做两件事（都只依赖颜色，不需要额外的原生接口）：
///
///   1. **程序化贴图**：按群系基色生成一张 16×16 的可平铺纹理（水面横纹 / 林地斑点 /
///      雪原细点 / 岩石颗粒…），按世界坐标取样后和基色相乘 —— 放大后不再是纯色块。
///   2. **群系描边**：相邻像素基色不同就算群系边界，那里压暗一点，
///      画面上会出现类似地图集那样的细边界线，层次立刻清楚。
///
/// 全部是确定性计算（同一个基色永远得到同一张纹理），不引入外部素材，也不改变群系本身的位置。
/// </summary>
public static class BiomeMapStyler
{
    /// <summary>纹理边长（像素）。取样时按世界坐标模 16，缩放时不会出现明显接缝。</summary>
    public const int TextureSize = 16;

    private static readonly ConcurrentDictionary<uint, byte[]> TextureCache = new();

    /// <summary>
    /// 就地加工 RGBA 缓冲区。
    /// </summary>
    /// <param name="rgba">像素（RGBA，行优先）。</param>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    /// <param name="worldLeft">缓冲区左上角对应的世界方块 X。</param>
    /// <param name="worldTop">缓冲区左上角对应的世界方块 Z。</param>
    /// <param name="blocksPerPixel">每像素多少方块（决定纹理在世界的尺度）。</param>
    /// <param name="textures">是否加贴图质感。</param>
    /// <param name="borders">是否画群系边界。</param>
    public static void Stylize(byte[] rgba, int width, int height,
        int worldLeft, int worldTop, int blocksPerPixel,
        bool textures = true, bool borders = true, bool hillshade = true)
    {
        if (rgba is null || width <= 1 || height <= 1) return;
        if (rgba.Length < width * height * 4) return;
        if (!textures && !borders && !hillshade) return;

        // 纹理在世界里的格子大小：放大视图时纹理粗一点，缩小时细一点
        // 纹理斑点的世界尺度：按 1 像素取，放大时更细，缩小时不会变成大色块
        var tileWorld = Math.Max(2, blocksPerPixel);

        // 近似高程：cubiomes 没有暴露地表高度，这里按群系类别给一个"高度档"，
        // 再加一点确定性起伏，用来做方向光阴影。**这是近似值**，界面上也要这么说。
        short[]? heights = null;

        if (hillshade)
        {
            heights = new short[width * height];
            for (var y = 0; y < height; y++)
            {
                var row = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var i = row + x * 4;
                    if (rgba[i + 3] == 0) continue;
                    heights[y * width + x] = ElevationOf(rgba[i], rgba[i + 1], rgba[i + 2], worldLeft + x, worldTop + y);
                }
            }
        }

        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;

            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var alpha = rgba[i + 3];
                if (alpha == 0) continue;

                var color = Pack(rgba[i], rgba[i + 1], rgba[i + 2]);

                // 方向光（左上打光）：取右邻与下邻的高差
                var shade = 128;

                if (heights is not null)
                {
                    var h = heights[y * width + x];
                    var hRight = x + 1 < width ? heights[y * width + x + 1] : h;
                    var hDown = y + 1 < height ? heights[(y + 1) * width + x] : h;
                    var slope = (hRight - h) + (hDown - h);

                    // 关键：坡度按"每方块"归一化，而不是按"每像素"。
                    // 否则放大/缩小时同一个群系的阴影强弱会变，看起来像换了颜色。
                    var strength = 1.6 / Math.Max(1, blocksPerPixel);
                    shade = (int)Math.Round(128 + slope * strength * 4.0);
                    shade = Math.Clamp(shade, 92, 164);
                }

                if (textures)
                {
                    var pattern = Texture(color);

                    // 用世界坐标取样：拖动/缩放时纹理跟着地形走，不会"浮"在屏幕上
                    var tx = (int)((worldLeft + x * blocksPerPixel) / tileWorld) & (TextureSize - 1);
                    var tz = (int)((worldTop + y * blocksPerPixel) / tileWorld) & (TextureSize - 1);
                    var factor = pattern[tz * TextureSize + tx];

                    // factor 是 0..255 的亮度百分比，128 = 原色
                    rgba[i + 0] = Scale(rgba[i + 0], factor);
                    rgba[i + 1] = Scale(rgba[i + 1], factor);
                    rgba[i + 2] = Scale(rgba[i + 2], factor);
                }

                if (shade != 128)
                {
                    rgba[i + 0] = Scale(rgba[i + 0], shade);
                    rgba[i + 1] = Scale(rgba[i + 1], shade);
                    rgba[i + 2] = Scale(rgba[i + 2], shade);
                }

                if (!borders) continue;

                // ── 边界与海岸线 ──
                // 旧做法是"右/下邻不同就压暗到 0.76"，1 像素硬边，缩小时像描了黑线。
                // 现在按 8 邻域里有多少个不同基色来算强度，压暗带过渡（0.94 → 0.86），
                // 并额外做海岸线：陆地靠水一侧提亮、水面靠陆一侧压暗，海岸会自己"浮"出来。
                var neighbours = 0;
                var sameEdge = false;
                var touchesWater = false;
                var selfWater = IsWater(rgba[i], rgba[i + 1], rgba[i + 2]);

                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= height) continue;

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;

                        var nx = x + dx;
                        if (nx < 0 || nx >= width) continue;

                        var j = (ny * width + nx) * 4;
                        var other = Pack(rgba[j], rgba[j + 1], rgba[j + 2]);

                        if (other == color) continue;

                        neighbours++;

                        if (Math.Abs(dx) + Math.Abs(dy) == 1) sameEdge = true;

                        if (IsWater(rgba[j], rgba[j + 1], rgba[j + 2]) != selfWater) touchesWater = true;
                    }
                }

                if (neighbours > 0)
                {
                    // 过渡带：直邻差异明显，斜邻轻微
                    var factor = sameEdge ? 0.86 : 0.94;
                    rgba[i + 0] = (byte)(rgba[i + 0] * factor);
                    rgba[i + 1] = (byte)(rgba[i + 1] * factor);
                    rgba[i + 2] = (byte)(rgba[i + 2] * factor);
                }

                if (touchesWater)
                {
                    // 陆地侧提亮（沙滩感），水面侧压暗（深度感）
                    var coast = selfWater ? 0.92 : 1.10;
                    rgba[i + 0] = (byte)Math.Clamp(rgba[i + 0] * coast, 0, 255);
                    rgba[i + 1] = (byte)Math.Clamp(rgba[i + 1] * coast, 0, 255);
                    rgba[i + 2] = (byte)Math.Clamp(rgba[i + 2] * coast, 0, 255);
                }
            }
        }
    }

    /// <summary>
    /// 按群系基色猜一个"高度档"（单位：相对格数，用于计算坡度，不是真实海拔）。
    /// 深水最低 → 海洋 → 河流/沙滩 → 平原/森林 → 丘陵/恶地 → 山地/雪峰 最高。
    /// </summary>
    /// <summary>按颜色判断是不是水域（新配色下海洋系都满足 b 明显大于 r）。</summary>
    private static bool IsWater(byte r, byte g, byte b) => b > r + 22 && b > g + 12;

    private static short ElevationOf(byte r, byte g, byte b, int worldX, int worldZ)
    {
        var brightness = (r * 299 + g * 587 + b * 114) / 1000;

        // 水：蓝色占优，越暗越深
        if (IsWater(r, g, b))
        {
            var depth = Math.Clamp(120 - brightness, 0, 120);
            return (short)(-14 - depth / 2);
        }

        // 雪原/雪峰：最亮且偏冷 → 最高
        if (brightness > 215 && b >= r) return (short)(96 + (brightness - 215));

        // 山地/石岸：偏灰、中等偏亮
        if (brightness > 110 && brightness < 190 && Math.Abs(r - g) < 22 && Math.Abs(g - b) < 22)
        {
            return (short)(52 + (brightness - 110) / 4);
        }

        // 恶地/沙漠：暖色、偏干 → 台地
        if (r > g + 12 && r > b + 24)
        {
            return (short)(34 + (r - g) / 4);
        }

        // 森林：绿占优 → 中等丘陵
        if (g > r + 10 && g > b + 6) return (short)(22 + (g - r) / 4);

        // 其余（平原、草地、沙滩…）
        var baseHeight = brightness > 180 ? 16 : 6;

        // 确定性起伏：让平原也有缓坡，阴影不会一片死平
        var ripple = (int)(Noise((uint)(worldX * 31 + worldZ * 17), 7, 11) & 0x0F);

        return (short)(baseHeight + ripple);
    }

    private static byte Scale(byte channel, int factor) =>
        (byte)Math.Clamp(channel * factor / 128, 0, 255);

    private static uint Pack(byte r, byte g, byte b) => (uint)((r << 16) | (g << 8) | b);

    /// <summary>按基色生成（并缓存）一张 16×16 的亮度模板。</summary>
    private static byte[] Texture(uint color) =>
        TextureCache.TryGetValue(color, out var cached) ? cached : TextureCache[color] = BuildTexture(color);

    private static byte[] BuildTexture(uint color)
    {
        var r = (int)((color >> 16) & 0xFF);
        var g = (int)((color >> 8) & 0xFF);
        var b = (int)(color & 0xFF);

        var pixel = new byte[TextureSize * TextureSize];
        var seed = color * 2654435761u;

        // 基调：亮度、饱和度决定用哪种纹样
        var brightness = (r * 299 + g * 587 + b * 114) / 1000;
        var water = b > r + 18 && b > g + 6;
        var snow = brightness > 205 && b >= r;
        var forest = g > r + 14 && g > b + 10;
        var dry = r > g + 16 && r > b + 30;
        var rock = Math.Abs(r - g) < 14 && Math.Abs(g - b) < 14 && brightness is > 60 and < 170;

        for (var y = 0; y < TextureSize; y++)
        {
            for (var x = 0; x < TextureSize; x++)
            {
                // 每个像素一个确定性的小噪声（-1..1）
                var noise = (Noise(seed, x, y) & 0xFF) / 255.0 - 0.5;

                double delta;

                if (water)
                {
                    // 水面：横向波纹 + 轻微噪声
                    var wave = Math.Sin((y + Noise(seed, 0, x) % 3) * Math.PI / 4.0);
                    delta = wave * 0.06 + noise * 0.03;
                }
                else if (snow)
                {
                    // 雪原：整体很亮，稀疏的小颗粒
                    delta = noise * 0.035 - 0.01;
                }
                else if (forest)
                {
                    // 林地：密集斑点，模拟树冠
                    delta = noise * 0.10 + (((x + y) & 3) == 0 ? -0.05 : 0.02);
                }
                else if (dry)
                {
                    // 沙漠/恶地：细碎沙粒
                    delta = noise * 0.11;
                }
                else if (rock)
                {
                    // 岩石/山峰：块状颗粒
                    delta = ((x & 1) == (y & 1) ? 0.05 : -0.05) + noise * 0.05;
                }
                else
                {
                    // 草地等：温和的斑驳
                    delta = noise * 0.07;
                }

                var value = (int)Math.Round(128 * (1.0 + delta));
                pixel[y * TextureSize + x] = (byte)Math.Clamp(value, 96, 168);
            }
        }

        return pixel;
    }

    /// <summary>确定性哈希（同一输入永远同一输出，便于测试与缓存）。</summary>
    private static uint Noise(uint seed, int x, int y)
    {
        var h = seed + (uint)(x * 374761393) + (uint)(y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        return h ^ (h >> 16);
    }
}
