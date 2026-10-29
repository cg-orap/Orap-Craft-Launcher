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
        var half = size / 2;
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0x0B, 0x0D, 0x11)), 0.8);
        var left = centre.X - half;
        var top = centre.Y - half;

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
        Columns,
        Arch,
        Spire,
        Eye,
        Cross,
    }

    private static Glyph GlyphOf(string key) => key switch
    {
        "village" => Glyph.House,
        "outpost" => Glyph.Tower,
        "monument" or "mansion" => Glyph.Roof,
        "desert_pyramid" or "jungle_temple" or "igloo" or "swamp_hut"
            or "ancient_city" or "trail_ruins" or "trial_chambers" => Glyph.Pyramid,
        "shipwreck" => Glyph.Boat,
        "treasure" => Glyph.Chest,
        "ocean_ruin" => Glyph.Columns,
        "ruined_portal" or "ruined_portal_nether" => Glyph.Arch,
        "fortress" or "bastion" => Glyph.Tower,
        "end_city" or "end_gateway" => Glyph.Spire,
        "stronghold" => Glyph.Eye,
        "spawn" => Glyph.Cross,
        _ => Glyph.Dot,
    };
}
