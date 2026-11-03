using System.Windows;
using System.Windows.Media;

namespace Orion.App.Controls;

/// <summary>
/// 16×16 亮色像素图标（自己画的，不使用 Minecraft 图像资源）。
///
/// 针对"看不出来是什么"重画的原则：
///  · **只用 2–4 个亮色**，形状占满 16×16，不堆细节；
///  · 主体色亮、饱和（橙木、亮紫、亮青、亮绿、亮红）；
///  · 深色只用于 1px 内描边；
///  · 白底板 + 灰细框，贴在深色地形上依然清楚。
/// </summary>
public static class PixelIcons
{
    private const int Grid = 16;

    private static readonly Dictionary<char, Color> Palette = new()
    {
        ['.'] = Colors.Transparent,
        ['o'] = Color.FromRgb(0x3A, 0x2E, 0x24),   // 内描边（唯一深色）
        ['a'] = Color.FromRgb(0xE8, 0xA9, 0x5E),   // 亮橡木
        ['A'] = Color.FromRgb(0xC4, 0x7C, 0x35),   // 橡木暗部
        ['r'] = Color.FromRgb(0xE0, 0x5A, 0x46),   // 亮红（屋顶/旗帜）
        ['R'] = Color.FromRgb(0xA8, 0x33, 0x28),   // 暗红（下界砖）
        ['w'] = Color.FromRgb(0xFF, 0xFF, 0xFF),   // 白（雪/眼白）
        ['g'] = Color.FromRgb(0x6E, 0xD8, 0x5E),   // 亮绿（末影/史莱姆）
        ['v'] = Color.FromRgb(0xC8, 0x9C, 0xFF),   // 亮紫（紫珀/鞘翅）
        ['V'] = Color.FromRgb(0x8A, 0x5A, 0xD8),   // 暗紫
        ['c'] = Color.FromRgb(0x5E, 0xE2, 0xE2),   // 亮青（海晶）
        ['C'] = Color.FromRgb(0x22, 0x92, 0x9C),   // 暗青
        ['y'] = Color.FromRgb(0xFF, 0xD6, 0x4A),   // 亮金
        ['m'] = Color.FromRgb(0xFF, 0x9A, 0x3C),   // 亮橙（火/铜）
        ['K'] = Color.FromRgb(0x3E, 0x44, 0x4E),   // 灰石
    };

    /// <summary>村庄：橙木墙 + 亮红屋顶 + 门。</summary>
    private static readonly string[] Village =
    [
        "................",
        ".......oo.......",
        "......orro......",
        ".....orrrro.....",
        "....orrrrrro....",
        "...orrrrrrrro...",
        "..oooooooooooo..",
        "..oaaaaaaaaaao..",
        "..oaaaaaaaaaao..",
        "..oaaooaaoaaao..",
        "..oaaooaaoaaao..",
        "..oaaooaaoaaao..",
        "..oaaooaaoaaao..",
        "..oooooooooooo..",
        "................",
        "................",
    ];

    /// <summary>掠夺者前哨站：高塔 + 红顶。</summary>
    private static readonly string[] Outpost =
    [
        "................",
        "......orro......",
        ".....orrrro.....",
        "......oooo......",
        "......oaao......",
        "......oaao......",
        ".....oaaaao.....",
        ".....oaaoao.....",
        ".....oaaoao.....",
        ".....oaaaao.....",
        ".....oaaoao.....",
        ".....oaaaao.....",
        "......oooo......",
        "................",
        "................",
        "................",
    ];

    /// <summary>要塞：末影之眼（眼白 + 亮绿虹膜 + 深瞳）。</summary>
    private static readonly string[] Stronghold =
    [
        "................",
        "....oooooooo....",
        "..oowwwwwwwwoo..",
        ".owwwwwwwwwwwwo.",
        ".owwwggggggwwwo.",
        ".owwggoggggogwo.",
        "owwggogggggogwo.",
        "owwggoogggoggwo.",
        "owwggggggggggwo.",
        "owwggggggggggwo.",
        ".owwggggggggwwo.",
        ".owwwggggggwwwo.",
        "..oowwwwwwwwoo..",
        "....oooooooo....",
        "................",
        "................",
    ];

    /// <summary>末地城：紫珀塔（亮紫，阶梯收分）。</summary>
    private static readonly string[] EndCity =
    [
        "................",
        ".......oo.......",
        "......ovvo......",
        "......ovvo......",
        ".....ovvvvo.....",
        ".....ovvvvo.....",
        "....ovvvvvvo....",
        "....oVVVVVVo....",
        "...ovvvvvvvvo...",
        "...oVVVVVVVVo...",
        "..ovvvvvvvvvvo..",
        "..oVVVVVVVVVVo..",
        ".ovvvvvvvvvvvvo.",
        ".oVVVVVVVVVVVVo.",
        ".oooooooooooooo.",
        "................",
    ];

    /// <summary>末地船：鞘翅（两片亮紫翅膀）。</summary>
    private static readonly string[] Elytra =
    [
        "................",
        "..vv........vv..",
        ".ovvvo....ovvvo.",
        ".ovvvvo..ovvvvo.",
        ".ovvvvvoovvvvvo.",
        ".ovvvvvvvvvvvvo.",
        "..ovvvvvvvvvvo..",
        "..oVVvvvvvvVVo..",
        "...oVVvvvvVVo...",
        "...oVVVvvVVVo...",
        "....oVVVVVVo....",
        "....oVVVVVVo....",
        ".....oVVVVo.....",
        "......oooo......",
        "................",
        "................",
    ];

    /// <summary>埋藏的宝藏：木箱 + 金锁。</summary>
    private static readonly string[] Treasure =
    [
        "................",
        "...oooooooooo...",
        "..oaaaaaaaaaao..",
        "..oaaaaaaaaaao..",
        "..oaaaaaaaaaao..",
        "..oooooooooooo..",
        "..oaaayyyyaaao..",
        "..oaaayyyyaaao..",
        "..oaaayyyyaaao..",
        "..oaaaaaaaaaao..",
        "..oaaayyyyaaao..",
        "..oaaaaaaaaaao..",
        "..oooooooooooo..",
        "................",
        "................",
        "................",
    ];

    /// <summary>沉船：船体 + 桅杆。</summary>
    private static readonly string[] Ship =
    [
        "................",
        ".......oo.......",
        ".......ao.......",
        ".......ao.......",
        "......oao.......",
        "......oao.......",
        "...oooooooooo...",
        "..oaaaaaaaaaao..",
        ".oaaaaaaaaaaaao.",
        ".oAAAaaaaaaAAAo.",
        "..oAAAAAAAAAAo..",
        "...oooooooooo...",
        "................",
        "................",
        "................",
        "................",
    ];

    /// <summary>雪屋：白色圆顶 + 亮青门洞。</summary>
    private static readonly string[] Igloo =
    [
        "................",
        "................",
        ".....oooooo.....",
        "...oowwwwwwoo...",
        "..owwwwwwwwwwo..",
        ".owwwwwwwwwwwwo.",
        ".owwwwwwwwwwwwo.",
        ".owwwwwwwwwwwwo.",
        ".owwwwccccwwwwo.",
        ".owwwcwwwwcwwwo.",
        ".owwwcwwwwcwwwo.",
        ".owwwwwwwwwwwwo.",
        "..owwwwwwwwwwo..",
        "...oooooooooo...",
        "................",
        "................",
    ];

    /// <summary>远古城市：幽匿块（亮青发光）。</summary>
    private static readonly string[] Sculk =
    [
        "................",
        "...oooooooooo...",
        "..oKKKKKKKKKKo..",
        "..oKccccccccKo..",
        "..oKcCCCCCCcKo..",
        "..oKcCccccCcKo..",
        "..oKcCcKKcCcKo..",
        "..oKcCccccCcKo..",
        "..oKcCCCCCCcKo..",
        "..oKccccccccKo..",
        "..oKKKKKKKKKKo..",
        "...oooooooooo...",
        "................",
        "................",
        "................",
        "................",
    ];

    /// <summary>下界要塞：暗砖桥 + 亮橙火光。</summary>
    private static readonly string[] Fortress =
    [
        "................",
        "................",
        "oooooooooooooooo",
        "oRRooRRooRRooRRo",
        "oRRooRRooRRooRRo",
        "oooooooooooooooo",
        ".oRo.mm..mm.oRo.",
        ".oRo.mm..mm.oRo.",
        ".oRo......oRo...",
        ".oRo......oRo...",
        ".oRo......oRo...",
        ".ooo......ooo...",
        "................",
        "................",
        "................",
        "................",
    ];

    /// <summary>海底神殿：亮青海晶石菱形。</summary>
    private static readonly string[] Monument =
    [
        "................",
        "......oooo......",
        ".....occcco.....",
        "....occcccco....",
        "...occcccccco...",
        "..occcCCCCccco..",
        ".occccCccCcccco.",
        ".occcCCCCCCccco.",
        ".occccCccCcccco.",
        "..occcCCCCccco..",
        "...occcccccco...",
        "....occcccco....",
        ".....occcco.....",
        "......oooo......",
        "................",
        "................",
    ];

    /// <summary>林地府邸：深木宅 + 两扇亮窗。</summary>
    private static readonly string[] Mansion =
    [
        "................",
        "....oooooooo....",
        "...oAAAAAAAao...",
        "..oAAAAAAAAAAo..",
        ".oAAAAAAAAAAAAo.",
        ".oAAAAAAAAAAAAo.",
        ".oAAyyAAAAyyAAo.",
        ".oAAyyAAAAyyAAo.",
        ".oAAAAAAAAAAAAo.",
        ".oAAyyAAAAyyAAo.",
        ".oAAyyAAAAyyAAo.",
        ".oAAAAAAAAAAAAo.",
        ".oAAAAAAAAAAAAo.",
        ".oooooooooooooo.",
        "................",
        "................",
    ];

    /// <summary>废弃传送门：黑曜石框 + 亮紫光。</summary>
    private static readonly string[] Portal =
    [
        "................",
        "...oooooooooo...",
        "..oKKKKKKKKKKo..",
        "..oKKvvvvvvKKo..",
        "..oKKvVVVVvKKo..",
        "..oKKvVvvVvKKo..",
        "..oKKvVvvVvKKo..",
        "..oKKvVvvVvKKo..",
        "..oKKvVVVVvKKo..",
        "..oKKvvvvvvKKo..",
        "..oKKKKKKKKKKo..",
        "...oooooooooo...",
        "................",
        "................",
        "................",
        "................",
    ];

    private static readonly Dictionary<string, string[]> Sprites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["village"] = Village,
        ["outpost"] = Outpost,
        ["mansion"] = Mansion,
        ["monument"] = Monument,
        ["desert_pyramid"] = Monument,
        ["jungle_temple"] = Sculk,
        ["swamp_hut"] = Village,
        ["igloo"] = Igloo,
        ["ocean_ruin"] = Monument,
        ["shipwreck"] = Ship,
        ["treasure"] = Treasure,
        ["ruined_portal"] = Portal,
        ["ruined_portal_nether"] = Portal,
        ["ancient_city"] = Sculk,
        ["trail_ruins"] = Sculk,
        ["trial_chambers"] = Fortress,
        ["stronghold"] = Stronghold,
        ["fortress"] = Fortress,
        ["bastion"] = Fortress,
        ["end_city"] = EndCity,
        ["end_gateway"] = Stronghold,
        ["end_ship"] = Elytra,
    };

    public static bool Has(string key) => Sprites.ContainsKey(key);

    /// <summary>把像素图画在 centre 为中心的 size×size 方框里。</summary>
    public static void Draw(DrawingContext dc, string key, Brush tint, Point centre, double size)
    {
        if (!Sprites.TryGetValue(key, out var rows)) rows = EndCity;

        var block = Math.Max(2.0, Math.Round(size / Grid));
        var span = block * Grid;
        var left = centre.X - span / 2;
        var top = centre.Y - span / 2;

        // 白底：把图标从深色地形里"托"出来（画面已经很深，不要再叠黑）
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)), null,
            new Rect(left, top, span, span));

        var brushes = new Dictionary<char, SolidColorBrush>();

        SolidColorBrush BrushOf(char c)
        {
            if (brushes.TryGetValue(c, out var cached)) return cached;

            var brush = new SolidColorBrush(Palette.TryGetValue(c, out var color) ? color : Colors.Magenta);
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

                dc.DrawRectangle(BrushOf(ch), null, new Rect(left + gx * block, top + gy * block, block, block));
            }
        }

        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0x8E, 0x96, 0x9E)), 1),
            new Rect(left - 0.5, top - 0.5, span + 1, span + 1));
    }
}
