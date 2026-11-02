using System.Windows;
using System.Windows.Media;

namespace Orion.App.Controls;

/// <summary>
/// 结构图标：把不同结构画成 10–12px 的矢量小图标，比统一圆点更容易一眼分辨。
///
/// 只用基本图元（矩形/三角形/折线），没有位图资源，任何 DPI 下都清晰。
/// </summary>
public static class StructureIconRenderer
{
    /// <summary>按结构 key 画一个图标（centre 为中心，size 为外接正方形边长）。</summary>
    public static void Draw(DrawingContext dc, string key, Brush brush, Point centre, double size)
    {
        // 统一成 12×12 逻辑网格：所有图标用同一套坐标基准，
        // 描边按尺寸等比缩放，缩小到 7px 时也不会糊成一团。
        size = Math.Clamp(size, 7, 16);

        var half = size / 2;
        var outlineWidth = Math.Max(0.6, size * 0.07);
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0x08, 0x0A, 0x0E)), outlineWidth);
        var left = centre.X - half;
        var top = centre.Y - half;

        // 底衬：小尺寸下让图标从地形色里"浮"出来
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)), null, centre, half * 1.05, half * 1.05);

        switch (GlyphOf(key))
        {
            case Glyph.House:
                dc.DrawGeometry(brush, outline, Geometry.Parse(
                    $"M {left},{top + size * 0.45} L {centre.X},{top} L {left + size},{top + size * 0.45} " +
                    $"L {left + size * 0.8},{top + size * 0.45} L {left + size * 0.8},{top + size} " +
                    $"L {left + size * 0.2},{top + size} L {left + size * 0.2},{top + size * 0.45} Z"));
                break;

            case Glyph.Tower:
                dc.DrawRectangle(brush, outline, new Rect(left + size * 0.2, top + size * 0.3, size * 0.6, size * 0.7));
                dc.DrawRectangle(brush, outline, new Rect(left + size * 0.1, top + size * 0.1, size * 0.25, size * 0.2));
                dc.DrawRectangle(brush, outline, new Rect(left + size * 0.65, top + size * 0.1, size * 0.25, size * 0.2));
                break;

            case Glyph.Roof:
                dc.DrawGeometry(brush, outline, Geometry.Parse(
                    $"M {left},{top + size * 0.6} L {centre.X},{top + size * 0.1} L {left + size},{top + size * 0.6} " +
                    $"L {left + size * 0.8},{top + size} L {left + size * 0.2},{top + size} Z"));
                break;

            case Glyph.Pyramid:
                dc.DrawGeometry(brush, outline, Geometry.Parse(
                    $"M {centre.X},{top} L {left + size},{top + size * 0.75} L {left},{top + size * 0.75} Z"));
                dc.DrawRectangle(brush, outline, new Rect(left + size * 0.15, top + size * 0.75, size * 0.7, size * 0.25));
                break;

            case Glyph.Boat:
                dc.DrawGeometry(brush, outline, Geometry.Parse(
                    $"M {left},{top + size * 0.55} L {left + size},{top + size * 0.55} L {left + size * 0.75},{top + size} " +
                    $"L {left + size * 0.25},{top + size} Z"));
                dc.DrawLine(new Pen(brush, 1.2), new Point(centre.X, top), new Point(centre.X, top + size * 0.55));
                break;

            case Glyph.Villager:
            {
                // 村民头像：方脑袋 + 眉 + 大鼻子（参考 MC 里的村民脸）
                dc.DrawRectangle(brush, outline,
                    new Rect(left + size * 0.08, top + size * 0.08, size * 0.84, size * 0.84));

                var dark = new SolidColorBrush(Color.FromArgb(0xDD, 0x1A, 0x1D, 0x24));
                var brow = new Pen(dark, Math.Max(0.7, size * 0.07));

                dc.DrawLine(brow, new Point(left + size * 0.24, top + size * 0.36),
                    new Point(left + size * 0.44, top + size * 0.4));
                dc.DrawLine(brow, new Point(left + size * 0.56, top + size * 0.4),
                    new Point(left + size * 0.76, top + size * 0.36));

                dc.DrawRectangle(dark, null,
                    new Rect(centre.X - size * 0.1, top + size * 0.44, size * 0.2, size * 0.34));
                break;
            }

            case Glyph.Elytra:
            {
                // 鞘翅：两片对称的翅膀
                dc.DrawGeometry(brush, outline, Geometry.Parse(
                    $"M {centre.X},{top + size * 0.12} L {left + size * 0.04},{top + size * 0.5} " +
                    $"L {centre.X - size * 0.04},{top + size} L {centre.X},{top + size * 0.55} Z"));
                dc.DrawGeometry(brush, outline, Geometry.Parse(
                    $"M {centre.X},{top + size * 0.12} L {left + size * 0.96},{top + size * 0.5} " +
                    $"L {centre.X + size * 0.04},{top + size} L {centre.X},{top + size * 0.55} Z"));
                break;
            }

            case Glyph.Chest:
                dc.DrawRectangle(brush, outline, new Rect(left + size * 0.1, top + size * 0.35, size * 0.8, size * 0.6));
                dc.DrawLine(new Pen(outline.Brush, 0.8), new Point(left + size * 0.1, top + size * 0.55),
                    new Point(left + size * 0.9, top + size * 0.55));
                break;

            case Glyph.Columns:
                dc.DrawRectangle(brush, outline, new Rect(left + size * 0.1, top + size * 0.2, size * 0.18, size * 0.8));
                dc.DrawRectangle(brush, outline, new Rect(left + size * 0.45, top + size * 0.2, size * 0.18, size * 0.8));
                dc.DrawRectangle(brush, outline, new Rect(left + size * 0.75, top + size * 0.2, size * 0.18, size * 0.8));
                break;

            case Glyph.Arch:
                dc.DrawGeometry(brush, outline, Geometry.Parse(
                    $"M {left + size * 0.1},{top + size} L {left + size * 0.1},{top + size * 0.4} " +
                    $"Q {centre.X},{top} {left + size * 0.9},{top + size * 0.4} " +
                    $"L {left + size * 0.9},{top + size} L {left + size * 0.7},{top + size} " +
                    $"L {left + size * 0.7},{top + size * 0.5} Q {centre.X},{top + size * 0.25} {left + size * 0.3},{top + size * 0.5} " +
                    $"L {left + size * 0.3},{top + size} Z"));
                break;

            case Glyph.Spire:
                dc.DrawGeometry(brush, outline, Geometry.Parse(
                    $"M {centre.X},{top} L {left + size * 0.8},{top + size * 0.7} L {left + size * 0.6},{top + size} " +
                    $"L {left + size * 0.4},{top + size} L {left + size * 0.2},{top + size * 0.7} Z"));
                break;

            case Glyph.Eye:
                var pen = new Pen(brush, 1.4);
                dc.DrawEllipse(null, pen, centre, half, half * 0.72);
                dc.DrawEllipse(brush, null, centre, half * 0.34, half * 0.34);
                break;

            case Glyph.Cross:
                var crossPen = new Pen(brush, 1.6);
                dc.DrawLine(crossPen, new Point(centre.X - half, centre.Y), new Point(centre.X + half, centre.Y));
                dc.DrawLine(crossPen, new Point(centre.X, centre.Y - half), new Point(centre.X, centre.Y + half));
                dc.DrawEllipse(null, crossPen, centre, half * 0.6, half * 0.6);
                break;

            default:
                dc.DrawEllipse(brush, outline, centre, half * 0.65, half * 0.65);
                break;
        }
    }

    private enum Glyph
    {
        Dot,
        House,
        Tower,
        Roof,
        Pyramid,
        Boat,
        Chest,
        Villager,
        Elytra,
        Columns,
        Arch,
        Spire,
        Eye,
        Cross,
    }

    private static Glyph GlyphOf(string key) => key switch
    {
        "village" => Glyph.Villager,
        "outpost" => Glyph.Tower,
        "monument" or "mansion" => Glyph.Roof,
        "desert_pyramid" or "jungle_temple" or "igloo" or "swamp_hut"
            or "ancient_city" or "trail_ruins" or "trial_chambers" => Glyph.Pyramid,
        "shipwreck" => Glyph.Boat,
        "treasure" => Glyph.Chest,
        "ocean_ruin" => Glyph.Columns,
        "ruined_portal" or "ruined_portal_nether" => Glyph.Arch,
        "fortress" or "bastion" => Glyph.Tower,
        "end_city" => Glyph.Spire,          // 末地城：塔
        "end_gateway" => Glyph.Arch,        // 末地折跃门：门框
        "end_ship" => Glyph.Elytra,         // 末地船：鞘翅（和末地城分开）
        "stronghold" => Glyph.Eye,
        "spawn" => Glyph.Cross,
        _ => Glyph.Dot,
    };
}
