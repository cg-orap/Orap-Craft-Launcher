using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orion.Core.World;

namespace Orion.App.Controls;

/// <summary>悬停信息：方块坐标、群系、是否史莱姆区块。</summary>
public sealed class BiomeHoverEventArgs(int blockX, int blockZ, string? nativeName, int nativeId, BiomeKind fallback, bool slime) : EventArgs
{
    public int BlockX { get; } = blockX;
    public int BlockZ { get; } = blockZ;

    /// <summary>原生引擎给出的中文群系名（不可用时为 null）。</summary>
    public string? NativeName { get; } = nativeName;

    public int NativeId { get; } = nativeId;
    public BiomeKind FallbackBiome { get; } = fallback;
    public bool Slime { get; } = slime;
}

/// <summary>
/// 简化版群系地图画布：把 <see cref="SimplifiedBiomeMap"/> 渲染成位图，再叠格子、史莱姆区块与坐标。
///
/// 交互：拖动平移（松手后才重新采样，拖动期间只做位移预览）、滚轮缩放（2/4/8/16/32 方块每像素）。
/// 明确标注"简化模型"：结果可复现，但与游戏内真实地形不保证一致。
/// </summary>
public sealed class BiomeMapCanvas : FrameworkElement
{
    private static readonly Typeface MonoFace = new("Consolas");
    private static readonly Brush Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x12, 0x18));
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private static readonly Brush AxisBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xA7, 0x8B, 0xFA));
    private static readonly Brush SlimeBrush = new SolidColorBrush(Color.FromArgb(0x88, 0x9C, 0xFF, 0x8A));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromArgb(0xD8, 0xF2, 0xF4, 0xF8));
    private static readonly Brush OriginBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD8, 0x6B));

    // 与 cubiomes 的 scale 取值对齐：每格 1/4/16/64 方块
    private static readonly int[] ZoomSteps = [1, 4, 16, 64];

    private long _seed;
    private int _zoomIndex = 2;          // 默认 16 方块/像素
    private int _centerBlockX;
    private int _centerBlockZ;

    private BitmapSource? _bitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private int _bitmapVersion;
    private bool _renderQueued;

    private bool _dragging;
    private Point _dragStart;
    private int _dragCenterX;
    private int _dragCenterZ;
    private Vector _dragOffset;

    public event EventHandler<BiomeHoverEventArgs>? HoverChanged;

    public BiomeMapCanvas()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Focusable = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public long Seed
    {
        get => _seed;
        set
        {
            if (_seed == value) return;
            _seed = value;
            RequestRender();
        }
    }

    /// <summary>cubiomes 版本表下标（0 = 最新）。原生引擎不可用时忽略。</summary>
    public int EngineVersionIndex { get; set; }

    public bool ShowSlime { get; set; } = true;
    public bool ShowGrid { get; set; } = true;

    public int BlocksPerPixel => ZoomSteps[_zoomIndex];

    public void Zoom(double factor)
    {
        var next = _zoomIndex + (factor >= 1 ? 1 : -1);
        var clamped = Math.Clamp(next, 0, ZoomSteps.Length - 1);
        if (clamped == _zoomIndex) return;
        _zoomIndex = clamped;
        RequestRender();
    }

    public void CenterOn(int blockX, int blockZ)
    {
        _centerBlockX = blockX;
        _centerBlockZ = blockZ;
        RequestRender();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RequestRender();
    }

    // ── 渲染（后台线程算像素，UI 线程建位图） ─────────────
    private void RequestRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _renderQueued = false;
            if (ActualWidth < 2 || ActualHeight < 2) return;

            // 位图尺寸必须与元素一致，否则右侧/底部会留下未绘制的深色条。
            var width = Math.Max(2, (int)Math.Round(ActualWidth));
            var height = Math.Max(2, (int)Math.Round(ActualHeight));
            var seed = _seed;
            var centerX = _centerBlockX;
            var centerZ = _centerBlockZ;
            var step = BlocksPerPixel;
            var versionIndex = EngineVersionIndex;
            var version = ++_bitmapVersion;

            Task.Run(() =>
            {
                // 有原生引擎就用真算法（cubiomes），否则退回简化模型。
                var pixels = CubiomesNative.RenderRgba(seed, versionIndex, CubiomesNative.DimOverworld,
                                                       centerX, centerZ, step, width, height)
                             ?? SimplifiedBiomeMap.RenderRgba(seed, centerX, centerZ, step, width, height);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (version != _bitmapVersion) return;    // 已被更新的请求取代
                    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                        ToBgra(pixels), width * 4);
                    bitmap.Freeze();
                    _bitmap = bitmap;
                    _bitmapWidth = width;
                    _bitmapHeight = height;
                    InvalidateVisual();
                }));
            });
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>渲染层输出 RGBA，WPF 的 Bgra32 需要交换 R/B 两个通道。</summary>
    private static byte[] ToBgra(byte[] rgba)
    {
        var bgra = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            bgra[i + 0] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i + 0];
            bgra[i + 3] = rgba[i + 3];
        }

        return bgra;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));

        var step = BlocksPerPixel;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (_bitmap is not null)
        {
            // 尺寸一致时按原尺寸贴图（像素对齐），否则拉伸铺满，避免出现深色空隙。
            var rect = _bitmapWidth == (int)Math.Round(width) && _bitmapHeight == (int)Math.Round(height)
                ? new Rect(_dragOffset.X, _dragOffset.Y, _bitmapWidth, _bitmapHeight)
                : new Rect(_dragOffset.X, _dragOffset.Y, width, height);
            dc.DrawImage(_bitmap, rect);
        }

        var left = _centerBlockX - (int)(width / 2 * step) + (int)(_dragOffset.X * step);
        var top = _centerBlockZ - (int)(height / 2 * step) + (int)(_dragOffset.Y * step);

        // 网格与坐标：每 16 区块（256 方块）一条主线
        if (ShowGrid)
        {
            const int gridBlocks = 256;
            var cellPixels = gridBlocks / (double)step;
            if (cellPixels >= 18)
            {
                var startX = (int)Math.Floor(left / (double)gridBlocks) * gridBlocks;
                for (var bx = startX; bx <= left + width * step; bx += gridBlocks)
                {
                    var x = (bx - left) / (double)step;
                    dc.DrawLine(new Pen(bx == 0 ? AxisBrush : GridBrush, 1), new Point(x, 0), new Point(x, height));
                    var label = new FormattedText($"{bx}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        MonoFace, 10, TextBrush, dpi);
                    if (x > 26) dc.DrawText(label, new Point(x + 3, 3));
                }

                var startZ = (int)Math.Floor(top / (double)gridBlocks) * gridBlocks;
                for (var bz = startZ; bz <= top + height * step; bz += gridBlocks)
                {
                    var y = (bz - top) / (double)step;
                    dc.DrawLine(new Pen(bz == 0 ? AxisBrush : GridBrush, 1), new Point(0, y), new Point(width, y));
                    var label = new FormattedText($"{bz}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        MonoFace, 10, TextBrush, dpi);
                    if (y > 14) dc.DrawText(label, new Point(3, y + 2));
                }
            }
        }

        // 史莱姆区块叠加（区块 16 方块；格子太小时不画）
        if (ShowSlime && step <= 8)
        {
            var chunkPixels = 16.0 / step;
            if (chunkPixels >= 4)
            {
                var firstChunkX = (int)Math.Floor(left / 16.0);
                var lastChunkX = (int)Math.Floor((left + width * step) / 16.0);
                var firstChunkZ = (int)Math.Floor(top / 16.0);
                var lastChunkZ = (int)Math.Floor((top + height * step) / 16.0);

                for (var cz = firstChunkZ; cz <= lastChunkZ; cz++)
                {
                    for (var cx = firstChunkX; cx <= lastChunkX; cx++)
                    {
                        if (!SeedMap.IsSlimeChunk(_seed, cx, cz)) continue;
                        var px = (cx * 16 - left) / (double)step;
                        var py = (cz * 16 - top) / (double)step;
                        dc.DrawRectangle(SlimeBrush, null, new Rect(px + 0.5, py + 0.5,
                            Math.Max(1, chunkPixels - 1), Math.Max(1, chunkPixels - 1)));
                    }
                }
            }
        }

        // 原点十字
        var ox = (0 - left) / (double)step;
        var oy = (0 - top) / (double)step;
        if (ox > -20 && ox < width + 20 && oy > -20 && oy < height + 20)
        {
            var pen = new Pen(OriginBrush, 1.4);
            dc.DrawLine(pen, new Point(ox - 9, oy), new Point(ox + 9, oy));
            dc.DrawLine(pen, new Point(ox, oy - 9), new Point(ox, oy + 9));
        }
    }

    // ── 交互 ───────────────────────────────────────────────
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragCenterX = _centerBlockX;
        _dragCenterZ = _centerBlockZ;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        var step = BlocksPerPixel;

        if (_dragging)
        {
            _dragOffset = position - _dragStart;
            _centerBlockX = _dragCenterX - (int)(_dragOffset.X * step);
            _centerBlockZ = _dragCenterZ - (int)(_dragOffset.Y * step);
            InvalidateVisual();
            return;
        }

        var left = _centerBlockX - (int)(ActualWidth / 2 * step);
        var top = _centerBlockZ - (int)(ActualHeight / 2 * step);
        var blockX = left + (int)(position.X * step);
        var blockZ = top + (int)(position.Y * step);

        var nativeId = CubiomesNative.Available
            ? CubiomesNative.BiomeAt(_seed, EngineVersionIndex, CubiomesNative.DimOverworld, step, blockX, 63, blockZ)
            : -1;

        HoverChanged?.Invoke(this, new BiomeHoverEventArgs(
            blockX, blockZ,
            nativeId >= 0 ? BiomeCatalog.FromCubiomesName(CubiomesNative.BiomeName(EngineVersionIndex, nativeId)) : null,
            nativeId,
            SimplifiedBiomeMap.Sample(_seed, blockX, blockZ),
            SeedMap.IsSlimeChunk(_seed, blockX >> 4, blockZ >> 4)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        _dragOffset = default;
        RequestRender();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Zoom(e.Delta > 0 ? 1.25 : 0.8);
        e.Handled = true;
    }
}
