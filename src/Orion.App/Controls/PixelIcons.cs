using System.Windows;
using System.Windows.Media;

namespace Orion.App.Controls;

/// <summary>
/// 16×16 像素风结构图标（自己画的，不使用 Minecraft 的任何图像资源）。
///
/// 每个图标用一张字符网格定义：每个字符代表一种颜色，绘制时按
/// <c>size / 16</c> 的整数倍铺方块，所以任何尺寸下都是清晰的像素风，
/// 和游戏里的物品图标"同一个语感"，但不涉及版权素材。
/// </summary>
public static class PixelIcons
{
    private const int Grid = 16;

    private static readonly Dictionary<char, Color> Palette = new()
    {
        ['.'] = Colors.Transparent,
        ['o'] = Color.FromRgb(0x1A, 0x1D, 0x24),   // 描边
        ['s'] = Color.FromRgb(0xC9, 0xB6, 0x9A),   // 皮肤
        ['S'] = Color.FromRgb(0xA8, 0x92, 0x76),   // 皮肤暗部
        ['b'] = Color.FromRgb(0x8A, 0x5A, 0x3C),   // 木头
        ['B'] = Color.FromRgb(0x5E, 0x3C, 0x26),   // 深木头
        ['g'] = Color.FromRgb(0x6F, 0xB0, 0x4A),   // 草/绿
        ['G'] = Color.FromRgb(0x45, 0x7C, 0x33),   // 深绿
        ['w'] = Color.FromRgb(0xE8, 0xEE, 0xF2),   // 雪/白
        ['W'] = Color.FromRgb(0xB8, 0xC4, 0xCC),   // 灰白
        ['y'] = Color.FromRgb(0xE0, 0xC0, 0x6A),   // 沙/金
        ['Y'] = Color.FromRgb(0xB0, 0x92, 0x48),   // 暗金
        ['r'] = Color.FromRgb(0xC0, 0x4A, 0x3A),   // 红
        ['R'] = Color.FromRgb(0x8A, 0x2E, 0x24),   // 暗红
        ['p'] = Color.FromRgb(0xA0, 0x7C, 0xC8),   // 紫（末地）
        ['P'] = Color.FromRgb(0x6E, 0x50, 0x92),   // 暗紫
        ['c'] = Color.FromRgb(0xC8, 0x8A, 0x5A),   // 铜
        ['C'] = Color.FromRgb(0x8E, 0x5E, 0x38),   // 暗铜
        ['k'] = Color.FromRgb(0x3A, 0x3A, 0x42),   // 黑石
        ['K'] = Color.FromRgb(0x24, 0x24, 0x2A),   // 深黑石
        ['e'] = Color.FromRgb(0x6C, 0xD0, 0x9A),   // 末影绿
        ['E'] = Color.FromRgb(0x2E, 0x7A, 0x58),   // 暗末影绿
        ['i'] = Color.FromRgb(0x9C, 0xC8, 0xE0),   // 冰蓝
    };

    /// <summary>村民头像：方脑袋 + 粗眉 + 大鼻子（最容易认的结构图标）。</summary>
    private static readonly string[] Villager =
    [
        "................",
        "....oooooooo....",
        "...osssssssso...",
        "..osssssssssso..",
        "..osssssssssso..",
        "..osooooooosso..",
        "..osssoossssso..",
        "..osssoossssso..",
        "..osssssssssso..",
        "..ossSssssSsso..",
        "..osssssssssso..",
        "..osssooooosso..",
        "..ossssoosssso..",
        "..osssssssssso..",
        "...oooooooooo...",
        "................",
    ];

    /// <summary>鞘翅（末地船）：两片对称翅膀。</summary>
    private static readonly string[] Elytra =
    [
        "................",
        "...pp......pp...",
        "..pPPp....pPPp..",
        "..pPPp....pPPp..",
        "..pPPPp..pPPPp..",
        "..pPPPp..pPPPp..",
        "...pPPPppPPPp...",
        "...pPPPPPPPp....",
        "....pPPPPPPp....",
        "....pPPPPPPp....",
        ".....pPPPPp.....",
        ".....pPPPPp.....",
        "......pPPp......",
        "......ppp.......",
        "................",
        "................",
    ];

    /// <summary>箱子（埋藏的宝藏 / 沉船）。</summary>
    private static readonly string[] Chest =
    [
        "................",
        "................",
        "...oooooooooo...",
        "..obbbbbbbbbbo..",
        "..obbbbbbbbbbo..",
        "..obbbbbbbbbbo..",
        "..obbbbbbbbbbo..",
        "..oooooooooooo..",
        "..obbbbbybbbbO..",
        "..obbbbyyyyybo..",
        "..obbbbbbbybbo..",
        "..obbbbbbbbbbo..",
        "..obbbbbbbbbbo..",
        "...oooooooooo...",
        "................",
        "................",
    ];

    /// <summary>紫珀塔（末地城）。</summary>
    private static readonly string[] Spire =
    [
        "................",
        ".......oo.......",
        "......oppo......",
        "......oppo......",
        ".....opppo......",
        ".....opppo......",
        "....oppppo......",
        "....oppppo......",
        "...oppppppo.....",
        "...oppppppo.....",
        "..oppppppppo....",
        "..oppppppppo....",
        ".opppppppppo....",
        ".oPPPPPPPPPo....",
        ".oooooooooo.....",
        "................",
    ];

    /// <summary>末影之眼（要塞）。</summary>
    private static readonly string[] Eye =
    [
        "................",
        "................",
        "....oooooo......",
        "..oowwwwwwoo....",
        ".owwwwwwwwwwo...",
        ".owwwEEEEwwwo...",
        "owwwEeeeeEwwwo..",
        "owwwEeooEewwwo..",
        "owwwEeeeeEwwwo..",
        ".owwwEEEEwwwo...",
        ".owwwwwwwwwwo...",
        "..oowwwwwwoo....",
        "....oooooo......",
        "................",
        "................",
        "................",
    ];

    /// <summary>陶土台地（恶地类结构 / 古迹废墟）。</summary>
    private static readonly string[] Terrace =
    [
        "................",
        "................",
        "..oooooooooo....",
        "..oyyyyyyyyo....",
        "..oyyyyyyyyo....",
        "..oooooooooo....",
        "...orrrrrrro....",
        "...orrrrrrro....",
        "...ooooooooo....",
        "....oyyyyyyo....",
        "....oyyyyyyo....",
        "....oooooooo....",
        "................",
        "................",
        "................",
        "................",
    ];

    /// <summary>下界砖（要塞 / 堡垒）。</summary>
    private static readonly string[] NetherBrick =
    [
        "................",
        "................",
        "..oooooooooooo..",
        "..oKKKKoKKKKKo..",
        "..oKKKKoKKKKKo..",
        "..oooooooooooo..",
        "..oKoKKKKKKoKo..",
        "..oKoKKKKKKoKo..",
        "..oooooooooooo..",
        "..oKKKKoKKKKKo..",
        "..oKKKKoKKKKKo..",
        "..oooooooooooo..",
        "................",
        "................",
        "................",
        "................",
    ];

    /// <summary>沉船（船形）。</summary>
    private static readonly string[] Boat =
    [
        "................",
        "................",
        "................",
        "......oo........",
        "......obo.......",
        "......obo.......",
        "......obo.......",
        "..oooooboooo....",
        ".obBBBBBBBBBo...",
        ".oBBBBBBBBBBo...",
        "..oBBBBBBBBo....",
        "...oooooooo.....",
        "................",
        "................",
        "................",
        "................",
    ];

    /// <summary>雪屋（冰屋）。</summary>
    private static readonly string[] Igloo =
    [
        "................",
        "................",
        "....oooooo......",
        "...owwwwwwo.....",
        "..owwwwwwwwo....",
        ".owwwwwwwwwwo...",
        ".owwwwwwwwwwo...",
        ".owwwwwwwwwwo...",
        ".owwwwiiwwwwvo..",
        ".owwwiwwwiwwwo..",
        ".owwwwwwwwwwo...",
        "..owwwwwwwwo....",
        "...oooooooo.....",
        "................",
        "................",
        "................",
    ];

    /// <summary>幽匿（远古城市）。</summary>
    private static readonly string[] Sculk =
    [
        "................",
        "................",
        "...oooooooo.....",
        "..oKKKKKKKKo....",
        "..oKEEEEEEK o...",
        "..oKEeeeeEKo....",
        "..oKEeooEEKo....",
        "..oKEeeeeEKo....",
        "..oKEEEEEEK o...",
        "..oKKKKKKKKo....",
        "...oooooooo.....",
        "................",
        "................",
        "................",
        "................",
        "................",
    ];

    private static readonly Dictionary<string, string[]> Sprites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["village"] = Villager,
        ["outpost"] = Villager,
        ["mansion"] = Spire,
        ["monument"] = NetherBrick,
        ["desert_pyramid"] = Terrace,
        ["jungle_temple"] = Sculk,
        ["swamp_hut"] = Terrace,
        ["igloo"] = Igloo,
        ["ocean_ruin"] = NetherBrick,
        ["shipwreck"] = Boat,
        ["treasure"] = Chest,
        ["ruined_portal"] = NetherBrick,
        ["ruined_portal_nether"] = NetherBrick,
        ["ancient_city"] = Sculk,
        ["trail_ruins"] = Terrace,
        ["trial_chambers"] = NetherBrick,
        ["stronghold"] = Eye,
        ["fortress"] = NetherBrick,
        ["bastion"] = NetherBrick,
        ["end_city"] = Spire,
        ["end_gateway"] = Eye,
        ["end_ship"] = Elytra,
    };

    public static bool Has(string key) => Sprites.ContainsKey(key);

    /// <summary>把像素图画在 centre 为中心的 size×size 方框里。</summary>
    public static void Draw(DrawingContext dc, string key, Brush tint, Point centre, double size)
    {
        if (!Sprites.TryGetValue(key, out var rows)) rows = Spire;

        // 尺寸对齐到整数倍，像素图在任何缩放档位都不会出现半像素模糊
        // 每格至少 2px：低于这个尺寸 16×16 的细节全丢，图标就变成一块色斑
        var block = Math.Max(2.0, Math.Round(size / Grid));
        var left = centre.X - block * Grid / 2;
        var top = centre.Y - block * Grid / 2;

        var outline = new SolidColorBrush(Color.FromArgb(0xE6, 0x05, 0x07, 0x0A));
        var brushes = new Dictionary<char, SolidColorBrush>();

        SolidColorBrush BrushOf(char c)
        {
            if (brushes.TryGetValue(c, out var cached)) return cached;

            var brush = Palette.TryGetValue(c, out var color) ? new SolidColorBrush(color) : new SolidColorBrush(Colors.Magenta);
            brush.Freeze();
            brushes[c] = brush;
            return brush;
        }

        for (var gy = 0; gy < Grid; gy++)
        {
            var row = gy < rows.Length ? rows[gy] : string.Empty;

            for (var gx = 0; gx < Grid; gx++)
            {
                var ch = gx < row.Length ? row[gx] : '.';
                if (ch == '.' || ch == ' ') continue;

                var rect = new Rect(left + gx * block, top + gy * block, block, block);
                dc.DrawRectangle(BrushOf(ch), null, rect);
            }
        }

        // 外框描边：像素图整体加 1px 轮廓，缩小时也不会糊进地形色里
        // 阴影 + 光晕：地形颜色复杂时，图标靠这两层仍然清楚
        var pad = block * Grid;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00)), null,
            new Rect(left - 2, top - 2, pad + 4, pad + 4));
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)), 1.2),
            new Rect(left - 1, top - 1, pad + 2, pad + 2));

        dc.DrawRectangle(null, new Pen(outline, 1.4), new Rect(left, top, pad, pad));
    }
}
