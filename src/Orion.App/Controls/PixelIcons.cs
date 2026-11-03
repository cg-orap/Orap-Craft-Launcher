using System.Windows;
using System.Windows.Media;

namespace Orion.App.Controls;

/// <summary>
/// 结构图标：参考 Axolotl 种子地图的做法 —— 不画结构本体，改用**代表性方块/物品**。
/// 方块统一画成等轴测立方体（顶亮、左中、右暗），一眼就是"MC 里的那种方块"；
/// 少数结构用物品形状（末影之眼、箱子、船、鞘翅、铁轨、弩）。
/// 全部代码绘制，不使用 Minecraft 的任何图像资源。
/// </summary>
public static class PixelIcons
{
    private const int Grid = 16;
    private static readonly Color Outline = Color.FromRgb(0x24, 0x1C, 0x16);

    private readonly record struct Block(Color Top, Color Left, Color Right);

    private static readonly Dictionary<string, Block> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["village"] = new(Color.FromRgb(0x7C,0xD8,0x6A), Color.FromRgb(0x3E,0xA8,0x52), Color.FromRgb(0x2C,0x84,0x40)),
        ["mansion"] = new(Color.FromRgb(0x6E,0x4A,0x2E), Color.FromRgb(0x4A,0x2E,0x1C), Color.FromRgb(0x35,0x1F,0x12)),
        ["monument"] = new(Color.FromRgb(0x8C,0xF0,0xE8), Color.FromRgb(0x3E,0xC8,0xC0), Color.FromRgb(0x22,0x96,0x96)),
        ["ocean_ruin"] = new(Color.FromRgb(0xA8,0xE8,0xE0), Color.FromRgb(0x62,0xB8,0xB0), Color.FromRgb(0x3E,0x8C,0x88)),
        ["desert_pyramid"] = new(Color.FromRgb(0xF2,0xD8,0x92), Color.FromRgb(0xD8,0xB4,0x62), Color.FromRgb(0xB0,0x8C,0x42)),
        ["desert_well"] = new(Color.FromRgb(0xE8,0xCC,0x84), Color.FromRgb(0xC8,0xA4,0x56), Color.FromRgb(0x9E,0x7C,0x36)),
        ["jungle_temple"] = new(Color.FromRgb(0x8A,0xB0,0x6E), Color.FromRgb(0x5E,0x82,0x4A), Color.FromRgb(0x40,0x5E,0x32)),
        ["trail_ruins"] = new(Color.FromRgb(0xD8,0xA8,0x7C), Color.FromRgb(0xB0,0x82,0x58), Color.FromRgb(0x84,0x5E,0x3C)),
        ["swamp_hut"] = new(Color.FromRgb(0x7A,0x8A,0x4E), Color.FromRgb(0x54,0x62,0x32), Color.FromRgb(0x3C,0x46,0x22)),
        ["igloo"] = new(Color.FromRgb(0xFF,0xFF,0xFF), Color.FromRgb(0xDC,0xE8,0xF0), Color.FromRgb(0xB4,0xC4,0xD0)),
        ["ancient_city"] = new(Color.FromRgb(0x4E,0xD8,0xC8), Color.FromRgb(0x22,0x8C,0x88), Color.FromRgb(0x14,0x5E,0x60)),
        ["trial_chambers"] = new(Color.FromRgb(0xF0,0xA8,0x62), Color.FromRgb(0xC8,0x7C,0x3C), Color.FromRgb(0x96,0x58,0x26)),
        ["fortress"] = new(Color.FromRgb(0x8E,0x36,0x30), Color.FromRgb(0x64,0x22,0x1E), Color.FromRgb(0x44,0x14,0x12)),
        ["bastion"] = new(Color.FromRgb(0x5A,0x52,0x5E), Color.FromRgb(0x3A,0x34,0x40), Color.FromRgb(0x26,0x20,0x2A)),
        ["ruined_portal"] = new(Color.FromRgb(0x3A,0x2A,0x52), Color.FromRgb(0x22,0x18,0x34), Color.FromRgb(0x14,0x0E,0x22)),
        ["ruined_portal_nether"] = new(Color.FromRgb(0x3A,0x2A,0x52), Color.FromRgb(0x22,0x18,0x34), Color.FromRgb(0x14,0x0E,0x22)),
        ["end_city"] = new(Color.FromRgb(0xDC,0xB4,0xF2), Color.FromRgb(0xB0,0x86,0xD0), Color.FromRgb(0x84,0x5E,0xA4)),
        ["end_gateway"] = new(Color.FromRgb(0xE8,0xE8,0xD0), Color.FromRgb(0xB8,0xB8,0x9E), Color.FromRgb(0x8A,0x8A,0x74)),
    };

    private const string EyeRows = "................|....oooooooo....|..oowwwwwwwwoo..|.owwwwwwwwwwwwo.|.owwwggggggwwwo.|.owwggoggggogwo.|owwggogggggogwo.|owwggoogggoggwo.|owwggggggggggwo.|owwggggggggggwo.|.owwggggggggwwo.|.owwwggggggwwwo.|..oowwwwwwwwoo..|....oooooooo....|................|................";
    private const string ChestRows = "................|...oooooooooo...|..oaaaaaaaaaao..|..oaaaaaaaaaao..|..oooooooooooo..|..oaaayyyyaaao..|..oaaayyyyaaao..|..oaaaaaaaaaao..|..oaaayyyyaaao..|..oaaaaaaaaaao..|..oaaaaaaaaaao..|..oooooooooooo..|................|................|................|................";
    private const string BoatRows = "................|................|................|.......ao.......|.......ao.......|..oooooooooooo..|..oaaaaaaaaaao..|.oaaaaaaaaaaaao.|.oAAAaaaaaaAAAo.|..oAAAAAAAAAAo..|...oooooooooo...|................|................|................|................|................";
    private const string ElytraRows = "................|..vv........vv..|.ovvvo....ovvvo.|.ovvvvo..ovvvvo.|.ovvvvvoovvvvvo.|.ovvvvvvvvvvvvo.|..oVVvvvvvvVVo..|...oVVvvvvVVo...|...oVVVvvVVVo...|....oVVVVVVo....|....oVVVVVVo....|.....oVVVVo.....|......oooo......|................|................|................";
    private const string RailRows = "................|................|..o..........o..|..oooooooooooo..|..o..........o..|..o..........o..|..oooooooooooo..|..o..........o..|..o..........o..|..oooooooooooo..|..o..........o..|................|................|................|................|................";
    private const string CrossbowRows = "................|....oooooooo....|...oaaaaaaaa o..|..oaa......aao..|..oa........ao..|.oaa...oo...aao.|.oa...oaa o..ao.|.oa..oa..ao..ao.|.oa..oa..ao..ao.|.oa..oa..ao..ao.|.oa...oaa o..ao.|.oaa...oo...aao.|..oa........ao..|..oaa......aao..|...oooooooooo...|................";

    public static bool Has(string key) => Blocks.ContainsKey(key) ||
        key is "stronghold" or "treasure" or "shipwreck" or "end_ship" or "mineshaft" or "outpost";

    public static void Draw(DrawingContext dc, string key, Brush tint, Point centre, double size)
    {
        var block = Math.Max(2.0, Math.Round(size / Grid));
        var span = block * Grid;
        var left = centre.X - span / 2;
        var top = centre.Y - span / 2;

        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00)), null,
            new Rect(left + 1.5, top + 2, span, span));

        var plate = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xDF, 0xE6, 0xEC),
            new Point(0, 0), new Point(0, 1));
        plate.Freeze();
        dc.DrawRectangle(plate, null, new Rect(left, top, span, span));

        if (Blocks.TryGetValue(key, out var colors)) DrawIsoCube(dc, colors, left, top, block);
        else DrawPixels(dc, key switch
        {
            "stronghold" => EyeRows,
            "treasure" => ChestRows,
            "shipwreck" => BoatRows,
            "end_ship" => ElytraRows,
            "mineshaft" => RailRows,
            "outpost" => CrossbowRows,
            _ => ChestRows,
        }, left, top, block);

        var light = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        var dark = new SolidColorBrush(Color.FromArgb(0x55, 0x30, 0x38, 0x40));

        dc.DrawLine(new Pen(light, 1), new Point(left, top), new Point(left + span, top));
        dc.DrawLine(new Pen(light, 1), new Point(left, top), new Point(left, top + span));
        dc.DrawLine(new Pen(dark, 1), new Point(left, top + span), new Point(left + span, top + span));
        dc.DrawLine(new Pen(dark, 1), new Point(left + span, top), new Point(left + span, top + span));

        var bezel = new LinearGradientBrush(Color.FromRgb(0xF2, 0xF6, 0xF9), Color.FromRgb(0x8A, 0x93, 0x9C),
            new Point(0, 0), new Point(1, 1));
        bezel.Freeze();
        dc.DrawRectangle(null, new Pen(bezel, 1.2), new Rect(left - 0.6, top - 0.6, span + 1.2, span + 1.2));
    }

    private static void DrawIsoCube(DrawingContext dc, Block colors, double left, double top, double block)
    {
        var pen = new Pen(new SolidColorBrush(Outline), 1);
        var topVertex = new Point(left + 8 * block, top + 2 * block);
        var rightVertex = new Point(left + 14 * block, top + 6 * block);
        var bottomVertex = new Point(left + 8 * block, top + 14 * block);
        var leftVertex = new Point(left + 2 * block, top + 6 * block);
        var centreVertex = new Point(left + 8 * block, top + 10 * block);

        dc.DrawGeometry(new SolidColorBrush(colors.Top), pen, Tri(topVertex, rightVertex, leftVertex, centreVertex));
        dc.DrawGeometry(new SolidColorBrush(colors.Left), pen, Tri(leftVertex, centreVertex, bottomVertex, new Point(left + 2 * block, top + 11 * block)));
        dc.DrawGeometry(new SolidColorBrush(colors.Right), pen, Tri(rightVertex, new Point(left + 14 * block, top + 11 * block), bottomVertex, centreVertex));
    }

    private static StreamGeometry Tri(params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], true, true);
            for (var i = 1; i < points.Length; i++) ctx.LineTo(points[i], true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static void DrawPixels(DrawingContext dc, string packed, double left, double top, double block)
    {
        var rows = packed.Split('|');
        var brushes = new Dictionary<char, SolidColorBrush>();

        SolidColorBrush BrushOf(char c)
        {
            if (brushes.TryGetValue(c, out var cached)) return cached;

            var color = c switch
            {
                'o' => Outline,
                'w' => Color.FromRgb(0xFF, 0xFF, 0xFF),
                'g' => Color.FromRgb(0x6E, 0xD8, 0x5E),
                'a' => Color.FromRgb(0xE8, 0xA9, 0x5E),
                'A' => Color.FromRgb(0xC4, 0x7C, 0x35),
                'y' => Color.FromRgb(0xFF, 0xD6, 0x4A),
                'v' => Color.FromRgb(0xC8, 0x9C, 0xFF),
                'V' => Color.FromRgb(0x8A, 0x5A, 0xD8),
                _ => Colors.Magenta,
            };

            var brush = new SolidColorBrush(color);
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
    }
}
