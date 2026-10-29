using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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
    // 细档位：每档 2 倍。cubiomes 只支持 1/4/16/64 的采样 scale，中间档用"渲染更大再缩小"实现。
    private static readonly int[] ZoomSteps = [1, 2, 4, 8, 16, 32, 64];

    // 一次点击只走一档，视觉上先缩放旧图（220ms 缓出），新图到达后再归位，避免生硬跳变。
    private const int ZoomAnimationMs = 220;

    private long _seed;
    private int _zoomIndex = 4;          // 默认 16 方块/像素
    private int? _pendingZoomIndex;
    private DispatcherTimer? _zoomTimer;
    private int _centerBlockX;
    private int _centerBlockZ;

    private BitmapSource? _bitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private int _bitmapVersion;
    private bool _renderQueued;
    private bool _renderAgainAfterLoad;
    private long _lastRenderStarted;

    private int _locateX;
    private int _locateZ;
    private bool _hasLocateMark;

    private bool _dragging;
    private Point _dragStart;
    private int _dragCenterX;
    private int _dragCenterZ;

    // 当前位图对应的中心与采样步长：拖动时旧图按中心差平移，避免空白/跳变
    private int _bitmapCenterX;
    private int _bitmapCenterZ;
    private int _bitmapStep = 16;

    private bool _loading;
    private DispatcherTimer? _inertiaTimer;
    private double _inertiaVx;
    private double _inertiaVy;
    private long _lastInertiaTick;

    // 惯性：记录最近两次移动的时间与位置，松手时换算成额外滑行像素
    private Point _lastMovePoint;
    private long _lastMoveTicks;
    private double _velocityX;   // px/ms
    private double _velocityY;
    private bool _fadeNextBitmap;

    // 缩放锚点：以鼠标位置为中心缩放（按钮/滚轮都有锚点，默认面板中心）
    private Point _zoomAnchor;
    private bool _hasZoomAnchor;
    private int _anchorBlockX;
    private int _anchorBlockZ;

    public event EventHandler<BiomeHoverEventArgs>? HoverChanged;

    /// <summary>缩放过渡用的视觉倍率（1 = 正常）。只影响绘制，不影响采样。</summary>
    public static readonly DependencyProperty VisualScaleProperty = DependencyProperty.Register(
        nameof(VisualScale), typeof(double), typeof(BiomeMapCanvas),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double VisualScale
    {
        get => (double)GetValue(VisualScaleProperty);
        set => SetValue(VisualScaleProperty, value);
    }

    /// <summary>新位图到达时的淡入透明度（拖动松手后用，避免硬切）。</summary>
    public static readonly DependencyProperty BitmapOpacityProperty = DependencyProperty.Register(
        nameof(BitmapOpacity), typeof(double), typeof(BiomeMapCanvas),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double BitmapOpacity
    {
        get => (double)GetValue(BitmapOpacityProperty);
        set => SetValue(BitmapOpacityProperty, value);
    }

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

    /// <summary>放大：看清更小的范围（每像素方块数变少）。</summary>
    public void ZoomIn() => ZoomStep(-1);

    /// <summary>缩小：看到更大的范围。</summary>
    public void ZoomOut() => ZoomStep(+1);

    /// <summary>兼容旧调用：factor >= 1 视为放大。</summary>
    public void Zoom(double factor)
    {
        if (factor >= 1) ZoomIn();
        else ZoomOut();
    }

    /// <summary>清除锚点：下一次缩放以面板中心为准（按钮点击时用）。</summary>
    public void ClearZoomAnchor() => _hasZoomAnchor = false;

    /// <summary>把某点设为缩放锚点（元素坐标）。鼠标滚轮会把指针位置传进来。</summary>
    public void SetZoomAnchor(Point point)
    {
        _zoomAnchor = point;
        _hasZoomAnchor = true;
    }

    /// <summary>按档位缩放：视觉先放大/缩小，等新瓦片渲染好再归位。</summary>
    private void ZoomStep(int delta)
    {
        var baseline = _pendingZoomIndex ?? _zoomIndex;
        var target = Math.Clamp(baseline + delta, 0, ZoomSteps.Length - 1);
        if (target == baseline) return;

        _pendingZoomIndex = target;

        // 锚点：优先用鼠标位置；没有的话用面板中心
        _zoomAnchor = _hasZoomAnchor ? _zoomAnchor : new Point(ActualWidth / 2, ActualHeight / 2);
        var (anchorBlockX, anchorBlockZ) = MapViewportMath.BlockAt(_centerBlockX, _centerBlockZ,
            (int)ActualWidth, (int)ActualHeight, BlocksPerPixel, _zoomAnchor.X, _zoomAnchor.Y);
        _anchorBlockX = anchorBlockX;
        _anchorBlockZ = anchorBlockZ;

        // 过渡倍率 = 旧视野 / 新视野：放大（目标方块/像素更少）时把旧图放大，
        // 新瓦片到达后归位，观感上就是"平滑推近"。
        var ratio = ZoomSteps[_zoomIndex] / (double)ZoomSteps[target];
        var animation = new DoubleAnimation(VisualScale, ratio, new Duration(TimeSpan.FromMilliseconds(ZoomAnimationMs)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(VisualScaleProperty, animation);

        _zoomTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ZoomAnimationMs),
        };
        _zoomTimer.Stop();
        _zoomTimer.Tick -= OnZoomTimerTick;
        _zoomTimer.Tick += OnZoomTimerTick;
        _zoomTimer.Start();
    }

    private void OnZoomTimerTick(object? sender, EventArgs e)
    {
        _zoomTimer?.Stop();
        if (_pendingZoomIndex is not { } target || target == _zoomIndex) return;
   // 视觉倍率保持动画终值，等新图到达后在回调里归位
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

    /// <summary>拖动/惯性期间用节流渲染：最多每 90ms 发起一次，避免堆积请求。</summary>
    private void RequestRenderThrottled()
    {
        if (_loading)
        {
            // 已经有一次在飞：只标记"还需要再刷一次"，等它回来再发
            _renderAgainAfterLoad = true;
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastRenderStarted < 90)
        {
            _renderAgainAfterLoad = true;
            return;
        }

        RequestRender();
    }

    /// <summary>定位到指定方块坐标（输入框跳转用），并在图上标一个十字。</summary>
    public void LocateTo(int blockX, int blockZ, bool mark = true)
    {
        _centerBlockX = blockX;
        _centerBlockZ = blockZ;
        if (mark)
        {
            _locateX = blockX;
            _locateZ = blockZ;
            _hasLocateMark = true;
        }

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

            _loading = true;
            _lastRenderStarted = Environment.TickCount64;
            InvalidateVisual();

            // 位图尺寸必须与元素一致，否则右侧/底部会留下未绘制的深色条。
            var width = Math.Max(2, (int)Math.Round(ActualWidth));
            var height = Math.Max(2, (int)Math.Round(ActualHeight));
            var seed = _seed;
            var centerX = _centerBlockX;
            var centerZ = _centerBlockZ;
            var step = BlocksPerPixel;
            var versionIndex = EngineVersionIndex;
            // cubiomes 只吃 1/4/16/64；中间档（2/8/32）用"采样 scale + 2 倍超采样"实现
            var renderScale = step >= 64 ? 64 : step >= 16 ? 16 : step >= 4 ? 4 : 1;
            var supersample = step / renderScale;      // 1 或 2
            var version = ++_bitmapVersion;

            Task.Run(() =>
            {
                // 有原生引擎就用真算法（cubiomes），否则退回简化模型。
                var pixelWidth = width * supersample;
                var pixelHeight = height * supersample;
                var pixels = CubiomesNative.RenderRgba(seed, versionIndex, CubiomesNative.DimOverworld,
                                                       centerX, centerZ, renderScale, pixelWidth, pixelHeight)
                             ?? SimplifiedBiomeMap.RenderRgba(seed, centerX, centerZ, renderScale, pixelWidth, pixelHeight);
                width = pixelWidth;
                height = pixelHeight;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (version != _bitmapVersion) return;    // 已被更新的请求取代
                    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                        ToBgra(pixels), width * 4);
                    bitmap.Freeze();
                    _bitmap = bitmap;
                    _bitmapWidth = width;
                    _bitmapHeight = height;
                    _bitmapCenterX = centerX;
                    _bitmapCenterZ = centerZ;
                    _bitmapStep = step;
                    _loading = false;
                    _pendingZoomIndex = null;
                    BeginAnimation(VisualScaleProperty, null);
                    VisualScale = 1.0;

                    if (_fadeNextBitmap)
                    {
                        _fadeNextBitmap = false;
                        BitmapOpacity = 0.80;
                        BeginAnimation(BitmapOpacityProperty, new DoubleAnimation(1.0,
                            new Duration(TimeSpan.FromMilliseconds(120))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                    }

                    InvalidateVisual();

                    if (_renderAgainAfterLoad)
                    {
                        _renderAgainAfterLoad = false;
                        RequestRenderThrottled();
                    }
                }));
            });
        }), DispatcherPriority.Background);
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

        var scaling = Math.Abs(VisualScale - 1.0) > 0.001;
        if (scaling)
        {
            dc.PushTransform(new ScaleTransform(VisualScale, VisualScale, _zoomAnchor.X, _zoomAnchor.Y));
        }

        var step = BlocksPerPixel;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (_bitmap is not null)
        {
            // 旧图按"位图中心 vs 当前中心"的差平移：拖动时画面跟着走，
            // 新瓦片到达前不会出现空白，也不会在替换瞬间跳位。
            var dx = _bitmapStep > 0 ? (_bitmapCenterX - _centerBlockX) / (double)_bitmapStep : 0;
            var dz = _bitmapStep > 0 ? (_bitmapCenterZ - _centerBlockZ) / (double)_bitmapStep : 0;

            dc.PushOpacity(BitmapOpacity);
            dc.DrawImage(_bitmap, new Rect(dx, dz, width, height));
            dc.Pop();
        }

        var left = _centerBlockX - (int)(width / 2 * step);
        var top = _centerBlockZ - (int)(height / 2 * step);

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

        // 定位标记
        if (_hasLocateMark)
        {
            var mx = (_locateX - left) / (double)step;
            var my = (_locateZ - top) / (double)step;
            if (mx > -40 && mx < width + 40 && my > -40 && my < height + 40)
            {
                var pinPen = new Pen(OriginBrush, 2);
                dc.DrawEllipse(null, pinPen, new Point(mx, my), 7, 7);
                dc.DrawLine(pinPen, new Point(mx - 12, my), new Point(mx + 12, my));
                dc.DrawLine(pinPen, new Point(mx, my - 12), new Point(mx, my + 12));
            }
        }

        if (scaling) dc.Pop();

        if (_loading)
        {
            var text = new FormattedText("正在加载…", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 11, TextBrush, dpi);
            var rect = new Rect(width - text.Width - 26, 10, text.Width + 16, text.Height + 8);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xCC, 0x14, 0x18, 0x1F)), null, rect, 6, 6);
            dc.DrawText(text, new Point(rect.X + 8, rect.Y + 4));
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
        _lastMovePoint = _dragStart;
        _lastMoveTicks = Environment.TickCount64;
        _velocityX = 0;
        _velocityY = 0;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Math.Abs(VisualScale - 1.0) > 0.001) return;   // 缩放过渡中不做悬停换算
        var position = e.GetPosition(this);
        var step = BlocksPerPixel;

        if (_dragging)
        {
            var now = Environment.TickCount64;
            var dx = position.X - _lastMovePoint.X;
            var dy = position.Y - _lastMovePoint.Y;
            var dt = Math.Max(1, now - _lastMoveTicks);
            _velocityX = dx / (double)dt;
            _velocityY = dy / (double)dt;
            _lastMovePoint = position;
            _lastMoveTicks = now;

            // 中心实时跟随指针：地图"跟手"，未加载区域在拖动过程中持续流式补齐
            var (centerX, centerZ) = MapViewportMath.DraggedCenter(_dragCenterX, _dragCenterZ,
                position.X - _dragStart.X, position.Y - _dragStart.Y, step);
            _centerBlockX = centerX;
            _centerBlockZ = centerZ;
            RequestRenderThrottled();
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

    /// <summary>惯性：中心按松手速度继续滑行，16ms 衰减 12%（约 140ms 停住），期间持续流式取图。</summary>
    private void OnInertiaTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        var dt = Math.Max(1, now - _lastInertiaTick);
        _lastInertiaTick = now;

        var step = BlocksPerPixel;
        _centerBlockX -= (int)Math.Round(_inertiaVx * dt * step);
        _centerBlockZ -= (int)Math.Round(_inertiaVy * dt * step);

        _inertiaVx *= 0.88;
        _inertiaVy *= 0.88;

        RequestRenderThrottled();
        InvalidateVisual();

        if (Math.Abs(_inertiaVx) < 0.02 && Math.Abs(_inertiaVy) < 0.02)
        {
            _inertiaTimer?.Stop();
            RequestRender();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        // 惯性：松手后中心继续按速度滑行一小段（在 140ms 内衰减），期间仍然流式请求瓦片
        _inertiaVx = _velocityX;
        _inertiaVy = _velocityY;
        if (Math.Abs(_inertiaVx) < 0.05 && Math.Abs(_inertiaVy) < 0.05)
        {
            RequestRenderThrottled();
            return;
        }

        _lastInertiaTick = Environment.TickCount64;
        _inertiaTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _inertiaTimer.Tick -= OnInertiaTick;
        _inertiaTimer.Tick += OnInertiaTick;
        _inertiaTimer.Start();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        SetZoomAnchor(e.GetPosition(this));   // 以鼠标位置为中心缩放
        if (e.Delta > 0) ZoomIn(); else ZoomOut();
        e.Handled = true;
    }
}
