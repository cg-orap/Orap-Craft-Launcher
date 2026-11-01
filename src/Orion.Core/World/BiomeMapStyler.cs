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
        var tileWorld = Math.Max(4, blocksPerPixel * 2);

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

                    // slope 每单位约 1 格；缩放后适当放大，避免高差太小看不出来
                    var strength = blocksPerPixel >= 16 ? 2.2 : 3.2;
                    shade = (int)Math.Round(128 + slope * strength);
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

                // 群系边界：右邻或下邻基色不同 → 压暗
                var isEdge = false;

                if (x + 1 < width && Pack(rgba[(row + (x + 1) * 4)], rgba[row + (x + 1) * 4 + 1], rgba[row + (x + 1) * 4 + 2]) != color)
                {
                    isEdge = true;
                }
                else if (y + 1 < height)
                {
                    var down = ((y + 1) * width + x) * 4;
                    if (Pack(rgba[down], rgba[down + 1], rgba[down + 2]) != color) isEdge = true;
                }

                if (!isEdge) continue;

                // 三个通道等比例压暗：只压某个通道会让水边出现一圈荧光蓝
                rgba[i + 0] = (byte)(rgba[i + 0] * 0.76);
                rgba[i + 1] = (byte)(rgba[i + 1] * 0.76);
                rgba[i + 2] = (byte)(rgba[i + 2] * 0.76);
            }
        }
    }

    /// <summary>
    /// 按群系基色猜一个"高度档"（单位：相对格数，用于计算坡度，不是真实海拔）。
    /// 深水最低 → 海洋 → 河流/沙滩 → 平原/森林 → 丘陵/恶地 → 山地/雪峰 最高。
    /// </summary>
    private static short ElevationOf(byte r, byte g, byte b, int worldX, int worldZ)
    {
        var brightness = (r * 299 + g * 587 + b * 114) / 1000;

        // 水：蓝色占优，越暗越深
        if (b > r + 18 && b > g + 6)
        {
            var depth = Math.Clamp(90 - brightness, 0, 90);
            return (short)(-depth / 3);
        }

        // 雪/山地：很亮且偏冷
        if (brightness > 205 && b >= r) return (short)(70 + (brightness - 205) / 2);

        // 恶地/沙漠：暖色，偏干
        if (r > g + 16 && r > b + 30) return (short)(28 + (r - g) / 6);

        // 森林：绿占优
        if (g > r + 14 && g > b + 10) return (short)(18 + (g - r) / 5);

        // 其余（平原、草地、沙滩…）
        var baseHeight = brightness > 180 ? 14 : 6;

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
