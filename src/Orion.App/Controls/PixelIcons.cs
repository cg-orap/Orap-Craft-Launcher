using System.Windows;
using System.Windows.Media;

namespace Orion.App.Controls;

/// <summary>
/// 结构图标（矢量绘制，不用像素网格、不使用 Minecraft 图像资源）。
///
/// 变更原因：16×16 字符网格能表达的形状太有限，村民脸、鞘翅这类细节画不出来。
/// 现在改成用 Path/几何图形直接画轮廓 —— 任意尺寸都清晰，形状可以画准。
///
/// 图标构成：
///  · 立体卡片：投影 → 渐变底板 → 内容 → 内斜角 → 渐变外框
///  · 内容：宝箱/船/眼/翅膀/村民脸用矢量轮廓；其余结构用"代表性材质的等轴测方块"
/// </summary>
public static class PixelIcons
{
    private static readonly Color Ink = Color.FromRgb(0x2A, 0x22, 0x1C);

    private readonly record struct Block(Color Top, Color Left, Color Right);

    private static readonly Dictionary<string, Block> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mansion"] = new(Color.FromRgb(0x6E,0x4A,0x2E), Color.FromRgb(0x4A,0x2E,0x1C), Color.FromRgb(0x35,0x1F,0x12)),
        ["monument"] = new(Color.FromRgb(0x8C,0xF0,0xE8), Color.FromRgb(0x3E,0xC8,0xC0), Color.FromRgb(0x22,0x96,0x96)),
        ["ocean_ruin"] = new(Color.FromRgb(0xA8,0xE8,0xE0), Color.FromRgb(0x62,0xB8,0xB0), Color.FromRgb(0x3E,0x8C,0x88)),
        ["desert_pyramid"] = new(Color.FromRgb(0xF2,0xD8,0x92), Color.FromRgb(0xD8,0xB4,0x62), Color.FromRgb(0xB0,0x8C,0x42)),
        ["jungle_temple"] = new(Color.FromRgb(0x8A,0xB0,0x6E), Color.FromRgb(0x5E,0x82,0x4A), Color.FromRgb(0x40,0x5E,0x32)),
        ["trail_ruins"] = new(Color.FromRgb(0xD8,0xA8,0x7C), Color.FromRgb(0xB0,0x82,0x58), Color.FromRgb(0x84,0x5E,0x3C)),
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

    public static bool Has(string key) => key is "village" or "outpost" or "stronghold" or "treasure"
        or "shipwreck" or "end_ship" or "mineshaft" or "swamp_hut" || Blocks.ContainsKey(key);

    public static void Draw(DrawingContext dc, string key, Brush tint, Point centre, double size)
    {
        var span = Math.Max(13.0, Math.Round(size));
        var left = centre.X - span / 2;
        var top = centre.Y - span / 2;
        var box = new Rect(left, top, span, span);

        // 卡片：投影 → 渐变底板
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00)), null,
            new Rect(left + 1.5, top + 2, span, span));

        var plate = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xDF, 0xE6, 0xEC),
            new Point(0, 0), new Point(0, 1));
        plate.Freeze();
        dc.DrawRectangle(plate, null, box);

        // 内容：留 12% 内边距
        var inner = new Rect(left + span * 0.12, top + span * 0.12, span * 0.76, span * 0.76);

        switch (key)
        {
            case "village": DrawVillager(dc, inner); break;
            case "stronghold":
            case "end_gateway": DrawEye(dc, inner); break;
            case "end_ship": DrawElytra(dc, inner); break;
            case "treasure": DrawChest(dc, inner); break;
            case "shipwreck": DrawBoat(dc, inner); break;
            case "outpost": DrawCrossbow(dc, inner); break;
            case "mineshaft": DrawRail(dc, inner); break;
            case "swamp_hut": DrawCauldron(dc, inner); break;
            default:
                if (Blocks.TryGetValue(key, out var colors)) DrawIsoCube(dc, colors, inner);
                else DrawIsoCube(dc, new Block(Color.FromRgb(0xC8, 0xC8, 0xC8), Color.FromRgb(0x9E, 0x9E, 0x9E), Color.FromRgb(0x76, 0x76, 0x76)), inner);
                break;
        }

        // 内斜角 + 渐变外框
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

    /// <summary>村民头像：方脸 + 粗眉 + 大鼻子 + 深色头发边。</summary>
    private static void DrawVillager(DrawingContext dc, Rect r)
    {
        var pen = new Pen(new SolidColorBrush(Ink), 1);
        var skin = new SolidColorBrush(Color.FromRgb(0xE8, 0xC8, 0xA6));
        var hair = new SolidColorBrush(Color.FromRgb(0x4A, 0x37, 0x28));
        var brow = new Pen(new SolidColorBrush(Ink), Math.Max(1.0, r.Height * 0.07));

        // 头发（顶条）
        dc.DrawRectangle(hair, pen, new Rect(r.X, r.Y, r.Width, r.Height * 0.22));

        // 脸
        dc.DrawRectangle(skin, pen, new Rect(r.X + r.Width * 0.04, r.Y + r.Height * 0.2, r.Width * 0.92, r.Height * 0.78));

        // 眉毛
        dc.DrawLine(brow, new Point(r.X + r.Width * 0.22, r.Y + r.Height * 0.42),
            new Point(r.X + r.Width * 0.42, r.Y + r.Height * 0.46));
        dc.DrawLine(brow, new Point(r.X + r.Width * 0.58, r.Y + r.Height * 0.46),
            new Point(r.X + r.Width * 0.78, r.Y + r.Height * 0.42));

        // 鼻子（竖直长条）
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xD2, 0xAA, 0x86)), pen,
            new Rect(r.X + r.Width * 0.42, r.Y + r.Height * 0.48, r.Width * 0.16, r.Height * 0.36));
    }

    /// <summary>末影之眼：眼白 + 绿虹膜 + 深瞳。</summary>
    private static void DrawEye(DrawingContext dc, Rect r)
    {
        var pen = new Pen(new SolidColorBrush(Ink), 1);
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);

        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF4, 0xF8, 0xFA)), pen, c, r.Width / 2, r.Height / 2);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x5E, 0xE0, 0x7E)), null, c, r.Width * 0.34, r.Height * 0.34);
        dc.DrawEllipse(new SolidColorBrush(Ink), null, c, r.Width * 0.13, r.Height * 0.13);
    }

    /// <summary>鞘翅：两片对称翅膀（贝塞尔轮廓）。</summary>
    private static void DrawElytra(DrawingContext dc, Rect r)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x5E, 0x3E, 0x86)), 1);
        var fill = new SolidColorBrush(Color.FromRgb(0xC0, 0x9C, 0xF0));
        var mid = new Point(r.X + r.Width / 2, r.Y + r.Height * 0.16);

        var leftWing = new StreamGeometry();
        using (var ctx = leftWing.Open())
        {
            ctx.BeginFigure(mid, true, true);
            ctx.BezierTo(new Point(r.X - r.Width * 0.05, r.Y + r.Height * 0.3),
                new Point(r.X - r.Width * 0.05, r.Y + r.Height * 0.85),
                new Point(r.X + r.Width * 0.44, r.Y + r.Height), true, false);
            ctx.LineTo(new Point(r.X + r.Width / 2, r.Y + r.Height * 0.5), true, false);
        }

        leftWing.Freeze();
        dc.DrawGeometry(fill, pen, leftWing);

        var rightWing = new StreamGeometry();
        using (var ctx = rightWing.Open())
        {
            ctx.BeginFigure(new Point(mid.X, mid.Y), true, true);
            ctx.BezierTo(new Point(r.X + r.Width * 1.05, r.Y + r.Height * 0.3),
                new Point(r.X + r.Width * 1.05, r.Y + r.Height * 0.85),
                new Point(r.X + r.Width * 0.56, r.Y + r.Height), true, false);
            ctx.LineTo(new Point(r.X + r.Width / 2, r.Y + r.Height * 0.5), true, false);
        }

        rightWing.Freeze();
        dc.DrawGeometry(fill, pen, rightWing);
    }

    /// <summary>宝箱：箱体 + 盖缝 + 金锁。</summary>
    private static void DrawChest(DrawingContext dc, Rect r)
    {
        var pen = new Pen(new SolidColorBrush(Ink), 1);
        var wood = new SolidColorBrush(Color.FromRgb(0xD8, 0x9E, 0x58));
        var dark = new SolidColorBrush(Color.FromRgb(0xA8, 0x6E, 0x30));

        dc.DrawRectangle(wood, pen, new Rect(r.X, r.Y + r.Height * 0.18, r.Width, r.Height * 0.82));
        dc.DrawRectangle(dark, pen, new Rect(r.X, r.Y + r.Height * 0.18, r.Width, r.Height * 0.26));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x4A)), pen,
            new Rect(r.X + r.Width * 0.4, r.Y + r.Height * 0.36, r.Width * 0.2, r.Height * 0.3));
    }

    /// <summary>船：梯形船体 + 桅杆 + 帆。</summary>
    private static void DrawBoat(DrawingContext dc, Rect r)
    {
        var pen = new Pen(new SolidColorBrush(Ink), 1);
        var wood = new SolidColorBrush(Color.FromRgb(0xD8, 0x9E, 0x58));

        var hull = new StreamGeometry();
        using (var ctx = hull.Open())
        {
            ctx.BeginFigure(new Point(r.X, r.Y + r.Height * 0.6), true, true);
            ctx.LineTo(new Point(r.X + r.Width, r.Y + r.Height * 0.6), true, false);
            ctx.LineTo(new Point(r.X + r.Width * 0.78, r.Y + r.Height), true, false);
            ctx.LineTo(new Point(r.X + r.Width * 0.22, r.Y + r.Height), true, false);
        }

        hull.Freeze();
        dc.DrawGeometry(wood, pen, hull);

        var mast = new Pen(new SolidColorBrush(Themed.Ink), Math.Max(1.2, r.Width * 0.07));
        dc.DrawLine(mast, new Point(r.X + r.Width / 2, r.Y + r.Height * 0.08), new Point(r.X + r.Width / 2, r.Y + r.Height * 0.6));

        var sail = new StreamGeometry();
        using (var ctx = sail.Open())
        {
            ctx.BeginFigure(new Point(r.X + r.Width / 2, r.Y + r.Height * 0.12), true, true);
            ctx.LineTo(new Point(r.X + r.Width * 0.92, r.Y + r.Height * 0.52), true, false);
            ctx.LineTo(new Point(r.X + r.Width / 2, r.Y + r.Height * 0.52), true, false);
        }

        sail.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xF4, 0xF0, 0xE4)), pen, sail);
    }

    /// <summary>弩（掠夺者前哨站）：横臂 + 弓弦 + 托。</summary>
    private static void DrawCrossbow(DrawingContext dc, Rect r)
    {
        var pen = new Pen(new SolidColorBrush(Ink), 1);
        var wood = new SolidColorBrush(Color.FromRgb(0xC8, 0x8A, 0x4E));

        dc.DrawRectangle(wood, pen, new Rect(r.X + r.Width * 0.34, r.Y + r.Height * 0.3, r.Width * 0.32, r.Height * 0.7));
        dc.DrawLine(new Pen(wood, Math.Max(1.5, r.Width * 0.12)), new Point(r.X, r.Y + r.Height * 0.34),
            new Point(r.X + r.Width, r.Y + r.Height * 0.34));
        dc.DrawLine(new Pen(new SolidColorBrush(Ink), 1), new Point(r.X, r.Y + r.Height * 0.34),
            new Point(r.X + r.Width / 2, r.Y + r.Height * 0.62));
        dc.DrawLine(new Pen(new SolidColorBrush(Ink), 1), new Point(r.X + r.Width, r.Y + r.Height * 0.34),
            new Point(r.X + r.Width / 2, r.Y + r.Height * 0.62));
    }

    /// <summary>铁轨（废弃矿井）：两条轨道 + 枕木。</summary>
    private static void DrawRail(DrawingContext dc, Rect r)
    {
        var rail = new Pen(new SolidColorBrush(Color.FromRgb(0xA8, 0xAE, 0xB6)), Math.Max(1.2, r.Width * 0.08));
        var tie = new SolidColorBrush(Color.FromRgb(0xB0, 0x86, 0x54));

        for (var i = 0; i < 4; i++)
        {
            var y = r.Y + r.Height * (0.16 + i * 0.22);
            dc.DrawRectangle(tie, null, new Rect(r.X + r.Width * 0.1, y, r.Width * 0.8, r.Height * 0.08));
        }

        dc.DrawLine(rail, new Point(r.X + r.Width * 0.26, r.Y), new Point(r.X + r.Width * 0.26, r.Y + r.Height));
        dc.DrawLine(rail, new Point(r.X + r.Width * 0.74, r.Y), new Point(r.X + r.Width * 0.74, r.Y + r.Height));
    }

    /// <summary>炼药锅（女巫小屋）：圆柱锅体 + 内部液体。</summary>
    private static void DrawCauldron(DrawingContext dc, Rect r)
    {
        var pen = new Pen(new SolidColorBrush(Ink), 1);
        var metal = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x6C));

        dc.DrawRectangle(metal, pen, new Rect(r.X + r.Width * 0.12, r.Y + r.Height * 0.3, r.Width * 0.76, r.Height * 0.62));
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x62, 0xC8, 0x54)), pen,
            new Point(r.X + r.Width / 2, r.Y + r.Height * 0.32), r.Width * 0.38, r.Height * 0.12);
    }

    /// <summary>等轴测立方体：顶面 + 左面 + 右面。</summary>
    private static void DrawIsoCube(DrawingContext dc, Block colors, Rect r)
    {
        var pen = new Pen(new SolidColorBrush(Ink), 1);
        var w = r.Width;
        var h = r.Height;

        var topVertex = new Point(r.X + w / 2, r.Y);
        var rightVertex = new Point(r.X + w, r.Y + h * 0.3);
        var bottomVertex = new Point(r.X + w / 2, r.Y + h);
        var leftVertex = new Point(r.X, r.Y + h * 0.3);
        var centreVertex = new Point(r.X + w / 2, r.Y + h * 0.62);

        dc.DrawGeometry(new SolidColorBrush(colors.Top), pen, Poly(topVertex, rightVertex, leftVertex, centreVertex));
        dc.DrawGeometry(new SolidColorBrush(colors.Left), pen, Poly(leftVertex, centreVertex, bottomVertex, new Point(r.X, r.Y + h * 0.72)));
        dc.DrawGeometry(new SolidColorBrush(colors.Right), pen, Poly(rightVertex, new Point(r.X + w, r.Y + h * 0.72), bottomVertex, centreVertex));
    }

    private static StreamGeometry Poly(params Point[] points)
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

    private static class Themed
    {
        public static readonly Color Ink = Color.FromRgb(0x2A, 0x22, 0x1C);
    }
}
