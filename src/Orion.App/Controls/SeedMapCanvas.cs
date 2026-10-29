using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Orion.Core.World;

namespace Orion.App.Controls;

/// <summary>悬停信息：区块坐标 + 是否史莱姆区块。</summary>
public sealed class SeedMapHoverEventArgs(int chunkX, int chunkZ, bool slime) : EventArgs
{
    public int ChunkX { get; } = chunkX;
    public int ChunkZ { get; } = chunkZ;
    public bool Slime { get; } = slime;
}

/// <summary>
/// 离线种子地图：按区块格子绘制，支持拖动平移、滚轮缩放、悬停查看坐标。
///
/// 画的是**精确**数据（史莱姆区块与原版公式逐位一致，见 SeedMapTests 里对真实 Java 输出的
/// 黄金值测试）。生物群系与地形没有画，因为没有完整的世界生成实现；界面上也如实标注。
/// </summary>
public sealed class SeedMapCanvas : FrameworkElement
{
    private static readonly Typeface MonoFace = new("Consolas");
    private static readonly Brush Background = new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1B));
    private static readonly Brush SlimeFill = new SolidColorBrush(Color.FromArgb(0xC8, 0x6F, 0xBF, 0x73));
    private static readonly Brush GridMinor = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private static readonly Brush GridMajor = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
    private static readonly Brush AxisBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xA7, 0x8B, 0xFA));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xE6, 0xE9, 0xEE));
    private static readonly Brush OriginBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xD9, 0xA8, 0x5C));

    private long _seed;
    private int _chunkRadius = 48;
    private int _centerX;
    private int _centerZ;

    private bool _dragging;
    private Point _dragStart;
    private int _dragStartCenterX;
    private int _dragStartCenterZ;

    public SeedMapCanvas()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Focusable = true;
    }

    public event EventHandler<SeedMapHoverEventArgs>? HoverChanged;

    public long Seed
    {
        get => _seed;
        set
        {
            if (_seed == value) return;
            _seed = value;
            InvalidateVisual();
        }
    }

    /// <summary>可视范围的半宽（区块数）。越大越"缩小"。</summary>
    public int ChunkRadius
    {
        get => _chunkRadius;
        set
        {
            var clamped = Math.Clamp(value, 8, 512);
            if (_chunkRadius == clamped) return;
            _chunkRadius = clamped;
            InvalidateVisual();
        }
    }

    public ChunkCoord Center => new(_centerX, _centerZ);

    public void CenterOn(int chunkX, int chunkZ)
    {
        _centerX = chunkX;
        _centerZ = chunkZ;
        InvalidateVisual();
    }

    public void Zoom(double factor) => ChunkRadius = (int)Math.Round(_chunkRadius / factor);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));

        // 每个区块的像素边长；太小时改成每隔 N 个区块画一条线，避免糊成一片。
        var cell = Math.Min(width, height) / (2.0 * _chunkRadius + 1);
        if (cell <= 0) return;

        var chunksX = (int)Math.Ceiling(width / cell);
        var chunksZ = (int)Math.Ceiling(height / cell);
        var left = _centerX - chunksX / 2;
        var top = _centerZ - chunksZ / 2;
        var gridStep = cell >= 7 ? 1 : cell >= 3 ? 4 : 16;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 背景网格 + 坐标轴
        for (var i = 0; i <= chunksX + 1; i++)
        {
            var chunkX = left + i;
            var x = (chunkX - left) * cell;
            var isAxis = chunkX == 0;
            if (isAxis || chunkX % gridStep == 0)
            {
                var pen = new Pen(isAxis ? AxisBrush : chunkX % (gridStep * 4) == 0 ? GridMajor : GridMinor, 1);
                dc.DrawLine(pen, new Point(x, 0), new Point(x, height));
            }
        }

        for (var i = 0; i <= chunksZ + 1; i++)
        {
            var chunkZ = top + i;
            var y = (chunkZ - top) * cell;
            var isAxis = chunkZ == 0;
            if (isAxis || chunkZ % gridStep == 0)
            {
                var pen = new Pen(isAxis ? AxisBrush : chunkZ % (gridStep * 4) == 0 ? GridMajor : GridMinor, 1);
                dc.DrawLine(pen, new Point(0, y), new Point(width, y));
            }
        }

        // 史莱姆区块
        for (var i = 0; i <= chunksX; i++)
        {
            for (var j = 0; j <= chunksZ; j++)
            {
                var chunkX = left + i;
                var chunkZ = top + j;
                if (!SeedMap.IsSlimeChunk(_seed, chunkX, chunkZ)) continue;

                var rect = new Rect((chunkX - left) * cell + 0.5, (chunkZ - top) * cell + 0.5,
                    Math.Max(1, cell - 1), Math.Max(1, cell - 1));
                dc.DrawRectangle(SlimeFill, null, rect);
            }
        }

        // 原点区块
        if (0 >= left && 0 < left + chunksX + 1 && 0 >= top && 0 < top + chunksZ + 1)
        {
            var pen = new Pen(OriginBrush, 1.5);
            dc.DrawRectangle(null, pen, new Rect((0 - left) * cell + 0.5, (0 - top) * cell + 0.5,
                Math.Max(2, cell - 1), Math.Max(2, cell - 1)));
        }

        // 坐标标注（方块坐标，跟游戏里 F3 一致）
        var labelStep = gridStep * 4;
        for (var i = 0; i <= chunksX + 1; i++)
        {
            var chunkX = left + i;
            if (chunkX % labelStep != 0) continue;

            var x = (chunkX - left) * cell;
            var text = new FormattedText($"{chunkX * 16}", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, MonoFace, 10, TextBrush, dpi);
            dc.DrawText(text, new Point(Math.Clamp(x + 3, 2, Math.Max(2, width - text.Width - 2)), 3));
        }

        for (var j = 0; j <= chunksZ + 1; j++)
        {
            var chunkZ = top + j;
            if (chunkZ % labelStep != 0) continue;

            var y = (chunkZ - top) * cell;
            var text = new FormattedText($"{chunkZ * 16}", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, MonoFace, 10, TextBrush, dpi);
            dc.DrawText(text, new Point(3, Math.Clamp(y + 2, 2, Math.Max(2, height - text.Height - 2))));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragStartCenterX = _centerX;
        _dragStartCenterZ = _centerZ;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);

        if (_dragging)
        {
            var cell = Math.Min(ActualWidth, ActualHeight) / (2.0 * _chunkRadius + 1);
            if (cell > 0)
            {
                var dx = (int)Math.Round((_dragStart.X - position.X) / cell);
                var dz = (int)Math.Round((_dragStart.Y - position.Y) / cell);
                _centerX = _dragStartCenterX + dx;
                _centerZ = _dragStartCenterZ + dz;
                InvalidateVisual();
            }
            return;
        }

        if (TryGetChunk(position, out var chunkX, out var chunkZ))
        {
            HoverChanged?.Invoke(this, new SeedMapHoverEventArgs(chunkX, chunkZ, SeedMap.IsSlimeChunk(_seed, chunkX, chunkZ)));
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Zoom(e.Delta > 0 ? 1.25 : 0.8);
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HoverChanged?.Invoke(this, new SeedMapHoverEventArgs(0, 0, false));
    }

    private bool TryGetChunk(Point position, out int chunkX, out int chunkZ)
    {
        chunkX = 0;
        chunkZ = 0;
        var cell = Math.Min(ActualWidth, ActualHeight) / (2.0 * _chunkRadius + 1);
        if (cell <= 0) return false;

        var chunksX = (int)Math.Ceiling(ActualWidth / cell);
        var chunksZ = (int)Math.Ceiling(ActualHeight / cell);
        var left = _centerX - chunksX / 2;
        var top = _centerZ - chunksZ / 2;

        chunkX = left + (int)(position.X / cell);
        chunkZ = top + (int)(position.Y / cell);
        return true;
    }
}
