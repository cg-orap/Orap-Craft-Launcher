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
public sealed class BiomeHoverEventArgs(int blockX, int blockZ, string? nativeName, int nativeId, BiomeKind fallback, bool slime, string? nearStructure) : EventArgs
{
    public int BlockX { get; } = blockX;
    public int BlockZ { get; } = blockZ;

    /// <summary>原生引擎给出的中文群系名（不可用时为 null）。</summary>
    public string? NativeName { get; } = nativeName;

    public int NativeId { get; } = nativeId;
    public BiomeKind FallbackBiome { get; } = fallback;
    public bool Slime { get; } = slime;

    /// <summary>指针附近的结构名（12px 内），没有则为 null。</summary>
    public string? NearStructure { get; } = nearStructure;
}

/// <summary>地图上的一个结构标记（结构点或要塞）。</summary>
public sealed record StructureMarker(string Key, string Label, int BlockX, int BlockZ, bool IsStronghold = false);

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
    /// <summary>缩放档位（方块/像素）。数组首尾就是上下限：最小 1（看清楚 1 格），最大 64（看大范围）。</summary>
    private static readonly int[] ZoomSteps = [1, 2, 3, 4, 6, 8, 12, 16];

    /// <summary>是否已到最大放大倍数（每像素 1 格）。</summary>
    public bool AtMaxZoom => _zoomIndex <= 0;

    /// <summary>是否已到最大缩小倍数（每像素 64 格）。</summary>
    public bool AtMinZoom => _zoomIndex >= ZoomSteps.Length - 1;

    /// <summary>当前缩放档位的说明文字。</summary>
    public string ZoomText => AtMaxZoom ? $"{BlocksPerPixel} 格/像素 ▲" : AtMinZoom ? $"{BlocksPerPixel} 格/像素 ▼" : $"{BlocksPerPixel} 格/像素";

    // 一次点击只走一档，视觉上先缩放旧图（220ms 缓出），新图到达后再归位，避免生硬跳变。
    private const int ZoomAnimationMs = 220;

    private long _seed;
    private int _zoomIndex = 4;          // 默认 16 方块/像素
    private int? _pendingZoomIndex;
    private DispatcherTimer? _zoomTimer;

    /// <summary>最近一次渲染失败的原因（界面可以用来提示）。</summary>
    private string? _lastError;
    private int _centerBlockX;
    private int _centerBlockZ;

    private BitmapSource? _bitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private int _bitmapVersion;
    private bool _renderQueued;
    private DispatcherTimer? _renderDebounce;
    private bool _renderAgainAfterLoad;
    private long _lastRenderStarted;

    private int _locateX;
    private int _locateZ;
    private bool _hasLocateMark;

    private int _dimension = CubiomesNative.DimOverworld;
    private bool _showStructures = true;
    private HashSet<string>? _allowedStructures;
    private readonly List<StructureMarker> _structures = [];

    /// <summary>单帧最多画多少个结构图标（太密时优先保留靠前的，也就是靠中心的）。</summary>
    private const int MaxMarkers = 90;

    private readonly HashSet<int> _drawndedup = [];
    private (int X, int Z)? _spawn;
    private string? _hoverStructure;

    private bool _dragging;
    private Point _dragStart;
    private int _dragCenterX;
    private int _dragCenterZ;

    // 当前位图对应的中心与采样步长：拖动时旧图按中心差平移，避免空白/跳变
    private int _bitmapCenterX;
    private int _bitmapCenterZ;
    private int _bitmapStep = 16;

    // 位图覆盖的世界方块范围（渲染时记下来）。
    // 只记像素数 + step 不够：换过缩放档位后，用"拉伸到整屏"的方式画旧图
    // 会把它挪错位置，画面上就会出现"一块清晰图压在粗图上"的接缝。
    private double _bitmapWorldLeft, _bitmapWorldTop, _bitmapWorldWidth, _bitmapWorldHeight;
    private double _previewWorldLeft, _previewWorldTop, _previewWorldWidth, _previewWorldHeight;

    // 预览层：整屏 1/4 分辨率的粗图，用来在拖动/缩放期间垫底，避免出现空白。
    private readonly BiomeTileLayer _tileLayer = new();

    /// <summary>
    /// 用瓦片渲染（默认开）。
    /// 前提已满足：原生 Range.x/z 的单位修正后，
    /// BiomeTileAlignmentTests 的"相邻瓦片边界像素必须连续"在 scale=1/4/16/64 全部通过。
    /// </summary>
    private bool _useTiles = true;

    private void InitTileLayer()
    {
        _tileLayer.Renderer = request => Task.Run<byte[]?>(() =>
        {
            var pixels = request.Pixels;

            var rgba = CubiomesNative.RenderRgba(request.Seed, request.VersionIndex, request.Dimension,
                request.CenterX, request.CenterZ, request.RenderStep, pixels, pixels)
                ?? SimplifiedBiomeMap.RenderRgba(request.Seed, request.CenterX, request.CenterZ,
                    request.RenderStep, pixels, pixels);

            if (rgba is null) return null;

            if (_styled)
            {
                var span = pixels * request.RenderStep;
                BiomeMapStyler.Stylize(rgba, pixels, pixels,
                    request.CenterX - span / 2, request.CenterZ - span / 2,
                    Math.Max(1, request.RenderStep), textures: true, borders: true, hillshade: true);
            }

            return ToBgra(rgba);
        });

        _tileLayer.RequestFactory = (renderStep, centerX, centerZ) => new BiomeTileLayer.TileRequest(
            _seed, EngineVersionIndex, _dimension, renderStep, 1, centerX, centerZ, BiomeTileLayer.TilePixels);

        _tileLayer.Updated = () => Dispatcher.BeginInvoke(new Action(() =>
        {
            _loading = false;
            InvalidateVisual();
        }));
    }

    private BitmapSource? _previewBitmap;
    private int _previewCenterX;
    private int _previewCenterZ;
    private int _previewStep = 16;
    private int _previewVersion;
    private bool _previewQueued;
    private long _previewStartedAt;

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
        InitTileLayer();
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

            // 换种子必须把瓦片缓存/预览作废：
            // 瓦片缓存的 key 只含 (档位, tileX, tileZ)，不含种子，
            // 不清的话新种子会直接命中旧种子的瓦片 —— 表现就是"换种子地图不变"。
            _tileLayer.Clear();
            _previewBitmap = null;
            _previewVersion++;
            _bitmap = null;
            _bitmapVersion++;
            _bitmapWorldWidth = 0;
            _structures.Clear();
            _spawn = null;

            RequestRender();
        }
    }

    /// <summary>cubiomes 版本表下标（0 = 最新）。原生引擎不可用时忽略。</summary>
    public int EngineVersionIndex { get; set; }

    /// <summary>当前维度（主世界 / 下界 / 末地）。切换会清空地图重画。</summary>
    public int Dimension
    {
        get => _dimension;
        set
        {
            if (_dimension == value) return;
            _dimension = value;
            _structures.Clear();
            _spawn = null;

            // 换维度必须把旧维度的瓦片/预览/整屏图全部作废，
            // 否则新维度的图会和旧维度的残影拼在一起（画面上会出现 L 形色带）。
            _tileLayer.Clear();
            _previewBitmap = null;
            _previewVersion++;
            _bitmap = null;
            _bitmapVersion++;
            _bitmapWorldWidth = 0;

            RequestRender();
        }
    }

    /// <summary>是否在地图上标出结构与出生点。</summary>
    public bool ShowStructures
    {
        get => _showStructures;
        set
        {
            if (_showStructures == value) return;
            _showStructures = value;
            if (value) _structures.Clear();
            InvalidateVisual();
        }
    }

    /// <summary>只显示这些结构 key（null/空 = 全部）。</summary>
    public void SetStructureFilter(IEnumerable<string>? keys)
    {
        _allowedStructures = keys is null ? null : new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        InvalidateVisual();
    }

    /// <summary>点击了结构图标（用于把坐标填进输入框并定位）。</summary>
    public event EventHandler<StructureMarker>? StructureClicked;

    private bool StructureVisible(string key) => _allowedStructures is null || _allowedStructures.Count == 0
        || _allowedStructures.Contains(key);

    public bool ShowSlime { get; set; } = true;

    private bool _styled = true;

    /// <summary>是否给群系图加贴图质感与描边（关掉就退回纯色块）。</summary>
    public bool Styled
    {
        get => _styled;
        set
        {
            if (_styled == value) return;
            _styled = value;
            RequestRender();
        }
    }
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

        var previousIndex = _zoomIndex;

        // 只做"绘制步长插值"这一层动画（200ms ease-out），
        // 档位本身已经切换，不存在第二层倍率，所以不会回弹。
        var fromStep = (double)ZoomSteps[previousIndex];
        _zoomIndex = target;

        StartZoomAnimation(fromStep, ZoomSteps[target]);
        ScheduleRenderAfterGesture();

        _zoomTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ZoomAnimationMs),
        };
        _zoomTimer.Stop();
        _zoomTimer.Tick -= OnZoomTimerTick;
        _zoomTimer.Tick += OnZoomTimerTick;
        _zoomTimer.Start();
    }

    /// <summary>
    /// 兜底：缩放动画结束后如果新图迟迟没来（渲染失败/超时），
    /// 把视觉缩放强制归位，避免整张地图被缩成一小块。
    /// </summary>
    private void OnZoomTimerTick(object? sender, EventArgs e)
    {
        _zoomTimer?.Stop();

        // 档位已经在 ZoomStep 里提交；这里只负责"新图迟迟不来就归位"的兜底
        _zoomWatchdog ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };

        _zoomWatchdog.Stop();
        _zoomWatchdog.Tick -= OnZoomWatchdogTick;
        _zoomWatchdog.Tick += OnZoomWatchdogTick;
        _zoomWatchdog.Start();
        if (_pendingZoomIndex is not { } target || target == _zoomIndex) return;
   // 视觉倍率保持动画终值，等新图到达后在回调里归位
    }

    /// <summary>
    /// 缩放动画结束后把视觉倍率平滑归位到 1。
    /// 以前是直接置 1，动画被打断时会"往回弹一下"。
    /// </summary>
    private void SettleZoom()
    {
        if (Math.Abs(VisualScale - 1.0) < 0.001)
        {
            BeginAnimation(VisualScaleProperty, null);
            VisualScale = 1.0;
            return;
        }

        BeginAnimation(VisualScaleProperty, new DoubleAnimation(VisualScale, 1.0,
            new Duration(TimeSpan.FromMilliseconds(150)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private double _animStep;
    private double _animFromStep;
    private double _animToStep;
    private long _animStartedAt;
    private DispatcherTimer? _zoomAnimTimer;

    /// <summary>绘制用的连续步长：缩放动画期间在旧档位与新档位之间插值。</summary>
    private double RenderStep => _zoomAnimTimer?.IsEnabled == true ? _animStep : BlocksPerPixel;

    /// <summary>
    /// 平滑缩放：把"绘制步长"从旧值插值到新值。
    /// 只作用在绘制换算上（瓦片仍按档位渲染），所以不存在"档位缩放 + 动画缩放"叠加，
    /// 也就不回弹。
    /// </summary>
    private void StartZoomAnimation(double from, double to)
    {
        _zoomAnimTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _zoomAnimTimer.Tick -= OnZoomAnimTick;
        _zoomAnimTimer.Tick += OnZoomAnimTick;

        _animFromStep = from;
        _animToStep = to;
        _animStep = from;
        _animStartedAt = Environment.TickCount64;
        _zoomAnimTimer.Start();
    }

    private void OnZoomAnimTick(object? sender, EventArgs e)
    {
        const double durationMs = 200;

        var t = Math.Clamp((Environment.TickCount64 - _animStartedAt) / durationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);      // ease-out cubic

        _animStep = _animFromStep + (_animToStep - _animFromStep) * eased;
        InvalidateVisual();

        if (t < 1) return;

        _zoomAnimTimer?.Stop();
        _animStep = _animToStep;
        InvalidateVisual();
    }

    private DispatcherTimer? _zoomWatchdog;

    private void OnZoomWatchdogTick(object? sender, EventArgs e)
    {
        _zoomWatchdog?.Stop();

        if (_loading) return;                                  // 还在渲染，再等等
        if (Math.Abs(VisualScale - 1.0) < 0.001) return;        // 已经归位

        BeginAnimation(VisualScaleProperty, null);
        VisualScale = 1.0;
        InvalidateVisual();
        RequestRender();
    }

    /// <summary>当前视图中心（方块坐标）——「扫描附近结构」用它当圆心。</summary>
    public (int X, int Z) CenterBlock => (_centerBlockX, _centerBlockZ);

    public void CenterOn(int blockX, int blockZ)
    {
        _centerBlockX = blockX;
        _centerBlockZ = blockZ;
        RequestRender();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        // 侧栏收起/展开会让画布变宽变窄：旧位图先被拉伸铺满（不闪白），
        // 新图到达后再淡入，过渡就顺了。
        if (_bitmap is not null) _fadeNextBitmap = true;

        // 尺寸变化时的重新渲染也走防抖，避免连续 Resize 把渲染请求打爆
        ScheduleRenderAfterGesture();
    }

    /// <summary>
    /// 手势期间不渲染：只靠位移把旧图挪过去（O(1)，天然 60fps）。
    /// 停下来 140ms 后再渲染一次，避免"边拖边算"导致的卡顿与请求堆积。
    /// </summary>
    private bool _renderPendingAfterGesture;
    private long _lastDragPreviewTicks;

    private void ScheduleRenderAfterGesture()
    {
        // 拖动/惯性期间**不**启动渲染。
        // 以前每次鼠标停顿 90ms 就会起一次大渲染，把 CPU 占满，
        // 用户继续拖时新渲染被 _loading 挡住 —— 观感就是"卡住、松手才跳过去"。
        if (_dragging || _inertiaTimer?.IsEnabled == true)
        {
            _renderPendingAfterGesture = true;

            // 拖动期间仍然**排瓦片**：瓦片渲染在后台线程，
            // UI 线程只做"把已有瓦片按新位置画出来"，所以既跟手又能持续补新区域。
            PlanTiles();
            InvalidateVisual();
            return;
        }

        _renderPendingAfterGesture = false;
        _renderDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _renderDebounce.Stop();
        _renderDebounce.Tick -= OnRenderDebounceTick;
        _renderDebounce.Tick += OnRenderDebounceTick;
        _renderDebounce.Start();
    }

    private void OnRenderDebounceTick(object? sender, EventArgs e)
    {
        _renderDebounce?.Stop();
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
    /// <summary>
    /// 渲染一张整屏 1/4 分辨率的粗图（约 1/16 的像素量），用于垫底。
    /// 拖动时高清图还没回来，画面底下有这层就不会露出空白。
    /// </summary>
    private void QueuePreviewRender()
    {
        if (_previewQueued) return;
        if (ActualWidth < 8 || ActualHeight < 8) return;

        _previewQueued = true;
        _previewStartedAt = Environment.TickCount64;

        var width = Math.Max(8, (int)Math.Round(ActualWidth) / 4);
        var height = Math.Max(8, (int)Math.Round(ActualHeight) / 4);

        // 覆盖范围按**画布尺寸**算。以前用降采样后的像素数乘 step，
        // 结果预览只覆盖 1/4 的画布，其余地方就是"空洞"。
        var viewWidth = Math.Max(8, (int)Math.Round(ActualWidth));
        var viewHeight = Math.Max(8, (int)Math.Round(ActualHeight));
        var centerX = _centerBlockX;
        var centerZ = _centerBlockZ;

        // 渲染请求按整数档位，动画只影响绘制
        var step = BlocksPerPixel;
        var seed = _seed;
        var versionIndex = EngineVersionIndex;
        var dimension = _dimension;
        var renderScale = step >= 64 ? 64 : step >= 16 ? 16 : step >= 4 ? 4 : 1;
        var supersample = Math.Clamp(step / renderScale, 1, 2);
        var version = Interlocked.Increment(ref _previewVersion);
        var styled = _styled;

        Task.Run(() =>
        {
            try
            {
                var pixelWidth = width * supersample;
                var pixelHeight = height * supersample;

                var pixels = CubiomesNative.RenderRgba(seed, versionIndex, dimension,
                                 centerX, centerZ, renderScale, pixelWidth, pixelHeight)
                             ?? SimplifiedBiomeMap.RenderRgba(seed, centerX, centerZ, renderScale, pixelWidth, pixelHeight)
                             ?? new byte[pixelWidth * pixelHeight * 4];

                if (styled)
                {
                    var worldLeft = centerX - (int)(viewWidth / 2.0 * step);
                    var worldTop = centerZ - (int)(viewHeight / 2.0 * step);
                    BiomeMapStyler.Stylize(pixels, pixelWidth, pixelHeight, worldLeft, worldTop,
                        Math.Max(1, step / supersample), textures: true, borders: false, hillshade: true);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _previewQueued = false;
                    if (version != _previewVersion) return;

                    var bitmap = BitmapSource.Create(pixelWidth, pixelHeight, 96, 96,
                        PixelFormats.Bgra32, null, ToBgra(pixels), pixelWidth * 4);
                    bitmap.Freeze();

                    _previewBitmap = bitmap;
                    _previewCenterX = centerX;
                    _previewCenterZ = centerZ;
                    _previewStep = step;

                    _previewWorldWidth = viewWidth * (double)step;
                    _previewWorldHeight = viewHeight * (double)step;
                    _previewWorldLeft = centerX - _previewWorldWidth / 2;
                    _previewWorldTop = centerZ - _previewWorldHeight / 2;
                    InvalidateVisual();
                }));
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                           or InvalidOperationException or ArgumentException)
            {
                Dispatcher.BeginInvoke(new Action(() => _previewQueued = false));
            }
        });
    }

    /// <summary>算出当前视口需要的瓦片并排队（缺哪块补哪块）。</summary>
    private void PlanTiles()
    {
        if (!_useTiles) return;
        if (ActualWidth < 8 || ActualHeight < 8) return;

        var step = BlocksPerPixel;
        var viewLeft = _centerBlockX - ActualWidth / 2.0 * step;
        var viewTop = _centerBlockZ - ActualHeight / 2.0 * step;

        _tileLayer.Plan(step, viewLeft, viewTop, ActualWidth * step, ActualHeight * step);
    }

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
            QueuePreviewRender();
            PlanTiles();
            InvalidateVisual();

            // 位图尺寸必须与元素一致，否则右侧/底部会留下未绘制的深色条。
            var downscale = BlocksPerPixel >= 8 ? 2 : 1;
            var width = Math.Max(2, (int)Math.Round(ActualWidth) / downscale);
            var height = Math.Max(2, (int)Math.Round(ActualHeight) / downscale);

            // 同上：覆盖范围要用画布尺寸，而不是降采样后的像素数
            var viewWidth = Math.Max(2, (int)Math.Round(ActualWidth));
            var viewHeight = Math.Max(2, (int)Math.Round(ActualHeight));
            var seed = _seed;
            var centerX = _centerBlockX;
            var centerZ = _centerBlockZ;
            var step = BlocksPerPixel;
            var versionIndex = EngineVersionIndex;
            var dimension = _dimension;
            var showStructures = _showStructures;
            // cubiomes 只吃 1/4/16/64；中间档（2/8/32）用"采样 scale + 2 倍超采样"实现
            var renderScale = step >= 64 ? 64 : step >= 16 ? 16 : step >= 4 ? 4 : 1;
            var supersample = Math.Clamp(step / renderScale, 1, 2);   // 最多 2 倍超采样

            // 像素预算：一张图超过约 160 万像素就放弃超采样，
            // 否则放大后每次重算都上百万像素，拖动直接卡住。
            if ((long)width * height * supersample * supersample > 1_600_000)
            {
                supersample = 1;
            }
            var version = ++_bitmapVersion;

            Task.Run(() =>
            {
              try
              {
                // 有原生引擎就用真算法（cubiomes），否则退回简化模型。
                var pixelWidth = width * supersample;
                var pixelHeight = height * supersample;

                // 注意：cubiomes 在 scale>1 时的采样相位与"被渲染区间的起点/宽度"有关，
                // 把一屏切成条带或瓦片都会在接缝处出现色块错位（实测过）。所以整屏必须一次调用，
                // 平滑靠"手势期间只做位移 + 停下后再渲染"来保证。
                byte[] pixels = [];

                if (!_useTiles)
                {
                pixels = CubiomesNative.RenderRgba(seed, versionIndex, dimension,
                                                       centerX, centerZ, renderScale, pixelWidth, pixelHeight)
                             ?? SimplifiedBiomeMap.RenderRgba(seed, centerX, centerZ, renderScale, pixelWidth, pixelHeight)
                             ?? new byte[pixelWidth * pixelHeight * 4];
                }

                // 质感：给纯色群系图加程序化贴图 + 群系描边（纯计算，不依赖额外原生接口）
                if (!_useTiles && _styled)
                {
                    var worldLeft = centerX - (int)(viewWidth / 2.0 * step);
                    var worldTop = centerZ - (int)(viewHeight / 2.0 * step);

                    BiomeMapStyler.Stylize(pixels, pixelWidth, pixelHeight,
                        worldLeft, worldTop, Math.Max(1, step / Math.Max(1, supersample)),
                        textures: true, borders: true);
                }

                // 结构与出生点跟着同一次渲染取，避免额外的时序问题
                List<StructureMarker> markers = [];
                (int X, int Z)? spawn = null;
                if (showStructures && CubiomesNative.Available)
                {
                    var step = RenderStep;
                    var leftChunk = (centerX - width / 2 * BlocksPerPixel) >> 4;
                    var rightChunk = (centerX + width / 2 * BlocksPerPixel) >> 4;
                    var topChunk = (centerZ - height / 2 * BlocksPerPixel) >> 4;
                    var bottomChunk = (centerZ + height / 2 * BlocksPerPixel) >> 4;

                    foreach (var (key, x, z) in CubiomesNative.FindStructures(seed, versionIndex, dimension,
                                 leftChunk, topChunk, rightChunk, bottomChunk))
                    {
                        markers.Add(new StructureMarker(key, StructureLabels.Resolve(key), x, z));
                    }

                    if (dimension == CubiomesNative.DimOverworld)
                    {
                        foreach (var (x, z) in CubiomesNative.FindStrongholds(seed, versionIndex))
                        {
                            markers.Add(new StructureMarker("stronghold", StructureLabels.Resolve("stronghold"), x, z, true));
                        }

                        spawn = CubiomesNative.Spawn(seed, versionIndex);
                    }
                    else if (dimension == CubiomesNative.DimEnd)
                    {
                        // 末地船是 End City 的一个 piece，单独取出来画（图标和末地城不同）
                        foreach (var (x, z) in CubiomesNative.FindEndShips(seed, versionIndex,
                                     leftChunk, topChunk, rightChunk, bottomChunk))
                        {
                            markers.Add(new StructureMarker("end_ship", StructureLabels.Resolve("end_ship"), x, z));
                        }
                    }
                }

                width = pixelWidth;
                height = pixelHeight;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (version != _bitmapVersion) return;    // 已被更新的请求取代
                    SettleZoom();
                    if (_useTiles)
                    {
                        // 瓦片模式：整屏只负责结构与出生点，像素交给瓦片层
                        _structures.Clear();
                        _structures.AddRange(markers);
                        _spawn = spawn;
                        _loading = false;
                        _pendingZoomIndex = null;
                        InvalidateVisual();
                        PlanTiles();
                        return;
                    }

                    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                        ToBgra(pixels), width * 4);
                    bitmap.Freeze();
                    _bitmap = bitmap;
                    _bitmapWidth = width;
                    _bitmapHeight = height;
                    _bitmapCenterX = centerX;
                    _bitmapCenterZ = centerZ;
                    _structures.Clear();
                    _structures.AddRange(markers);
                    _spawn = spawn;
                    _bitmapStep = step;

                    // 注意：这里的 width/height 是位图像素数，乘 step 得到覆盖的方块范围
                    _bitmapWorldWidth = viewWidth * (double)step;
                    _bitmapWorldHeight = viewHeight * (double)step;
                    _bitmapWorldLeft = centerX - _bitmapWorldWidth / 2;
                    _bitmapWorldTop = centerZ - _bitmapWorldHeight / 2;
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
              }
              catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                             or InvalidOperationException or ArgumentException)
              {
                  // 渲染失败也必须把状态复位，否则 VisualScale 会卡在缩放值上（地图被缩成一小块）
                  Orion.Core.Diagnostics.AppLog.Warn("seedmap", "render-failed", ("error", ex.Message));

                  Dispatcher.BeginInvoke(new Action(() =>
                  {
                      _loading = false;
                      _pendingZoomIndex = null;
                      BeginAnimation(VisualScaleProperty, null);
                      VisualScale = 1.0;
                      _lastError = "地图渲染失败：" + ex.Message;
                      InvalidateVisual();

                      if (_renderAgainAfterLoad)
                      {
                          _renderAgainAfterLoad = false;
                          RequestRenderThrottled();
                      }
                  }));
              }
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

        var step = RenderStep;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 当前视口左上角对应的世界方块
        var viewLeft = _centerBlockX - width / 2.0 * step;
        var viewTop = _centerBlockZ - height / 2.0 * step;

        _drawndedup.Clear();

        // 预览层先垫底：拖出去的那部分不会是空白，而是"稍旧的粗图"
        if (_previewBitmap is not null && _previewWorldWidth > 0)
        {
            var rect = new Rect(
                (_previewWorldLeft - viewLeft) / step,
                (_previewWorldTop - viewTop) / step,
                _previewWorldWidth / step,
                _previewWorldHeight / step);

            dc.PushOpacity(1.0);
            dc.DrawImage(_previewBitmap, rect);
            dc.Pop();
        }

        if (_useTiles)
        {
            var tiles = _tileLayer.Visible((int)Math.Round(step), viewLeft, viewTop, width * step, height * step);

            foreach (var (bitmap, worldLeft, worldTop, spanBlocks) in tiles)
            {
                var rect = new Rect(
                    (worldLeft - viewLeft) / step,
                    (worldTop - viewTop) / step,
                    spanBlocks / step,
                    spanBlocks / step);

                // 像素级对齐，避免瓦片之间出现 1px 的缝
                rect = new Rect(Math.Round(rect.X), Math.Round(rect.Y),
                    Math.Round(rect.Width + rect.X) - Math.Round(rect.X),
                    Math.Round(rect.Height + rect.Y) - Math.Round(rect.Y));

                dc.DrawImage(bitmap, rect);
            }
        }
        else if (_bitmap is not null && _bitmapWorldWidth > 0)
        {
            // 按"这张图覆盖的方块范围"落到屏幕：换过缩放档位也能正确叠放，
            // 不会出现"清晰图只盖住一块、周围露粗图"的接缝。
            var rect = new Rect(
                (_bitmapWorldLeft - viewLeft) / step,
                (_bitmapWorldTop - viewTop) / step,
                _bitmapWorldWidth / step,
                _bitmapWorldHeight / step);

            // 兜底：如果这张图比视口小（比如刚切页签时画布还没撑开就渲染了），
            // 宁可拉伸铺满整屏，也不要让用户看到"中间一小块地图 + 外面一片黑"。
            var covers = rect.Width >= width - 1 && rect.Height >= height - 1;

            if (!covers)
            {
                rect = new Rect(0, 0, width, height);

                // 同时立刻补一次正常渲染，拉伸只是过渡
                if (!_loading) RequestRender();
            }

            dc.PushOpacity(BitmapOpacity);
            dc.DrawImage(_bitmap, rect);
            dc.Pop();
        }

        var left = _centerBlockX - (int)(width / 2 * step);
        var top = _centerBlockZ - (int)(height / 2 * step);

        // 网格与坐标：主网格 512 格（粗线 + 标注），次网格 128 格（细线、无字）
        if (ShowGrid)
        {
            const int majorBlocks = 512;
            const int minorBlocks = 128;

            var majorPixels = majorBlocks / step;
            var minorPixels = minorBlocks / step;

            var majorPen = new Pen(GridBrush, 1);
            var minorPen = new Pen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), 0.6);

            // 次网格：只在像素间距够大时才画，避免缩小时糊成灰罩
            if (minorPixels >= 14)
            {
                var startX = (int)Math.Floor(left / (double)minorBlocks) * minorBlocks;
                for (var bx = startX; bx <= left + width * step; bx += minorBlocks)
                {
                    if (bx % majorBlocks == 0) continue;
                    var x = (bx - left) / step;
                    dc.DrawLine(minorPen, new Point(x, 0), new Point(x, height));
                }

                var startZ = (int)Math.Floor(top / (double)minorBlocks) * minorBlocks;
                for (var bz = startZ; bz <= top + height * step; bz += minorBlocks)
                {
                    if (bz % majorBlocks == 0) continue;
                    var y = (bz - top) / step;
                    dc.DrawLine(minorPen, new Point(0, y), new Point(width, y));
                }
            }

            if (majorPixels >= 26)
            {
                var startX = (int)Math.Floor(left / (double)majorBlocks) * majorBlocks;
                for (var bx = startX; bx <= left + width * step; bx += majorBlocks)
                {
                    var x = (bx - left) / step;
                    dc.DrawLine(new Pen(bx == 0 ? AxisBrush : GridBrush, bx == 0 ? 1.4 : 1), new Point(x, 0), new Point(x, height));

                    // 标注贴顶，并在下方加一条浅色描边底衬，压在地形上也能看清
                    var label = new FormattedText($"{bx}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        MonoFace, 10, TextBrush, dpi);

                    if (x > 30)
                    {
                        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x66, 0x0B, 0x0D, 0x11)), null,
                            new Rect(x + 2, 2, label.Width + 2, label.Height));
                        dc.DrawText(label, new Point(x + 3, 3));
                    }
                }

                var startZ = (int)Math.Floor(top / (double)majorBlocks) * majorBlocks;
                for (var bz = startZ; bz <= top + height * step; bz += majorBlocks)
                {
                    var y = (bz - top) / step;
                    dc.DrawLine(new Pen(bz == 0 ? AxisBrush : GridBrush, bz == 0 ? 1.4 : 1), new Point(0, y), new Point(width, y));

                    var label = new FormattedText($"{bz}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        MonoFace, 10, TextBrush, dpi);

                    if (y > 16)
                    {
                        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x66, 0x0B, 0x0D, 0x11)), null,
                            new Rect(2, y + 1, label.Width + 2, label.Height));
                        dc.DrawText(label, new Point(3, y + 2));
                    }
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
        // 缩小到一定程度（step > 16，即 24/32/48/64 格每像素）时不画结构：
        // 视野里结构太多，图标会挤成一片把地形盖住 —— 这种时候不显示。
        if (_showStructures && step <= 8)
        {
            foreach (var marker in _structures)
            {
                if (!StructureVisible(marker.Key)) continue;

                var mx = (marker.BlockX - left) / (double)step;
                var my = (marker.BlockZ - top) / (double)step;
                if (mx < -12 || mx > width + 12 || my < -12 || my > height + 12) continue;

                // 图标随缩放变大变小：缩得很远时不至于糊成一片
                // 图标要"看得见"：以前缩得很远时会掉到 7px，贴在花花绿绿的地形上根本认不出。
                // 现在按档位给 13–20px，缩小视图时反而更大一点（信息密度低于放大时，能用空间换可读性）。
                var iconSize = step >= 48 ? 13.0 : step >= 16 ? 15.0 : 18.0;
                if (marker.IsStronghold) iconSize += 2;

                StructureIconRenderer.Draw(dc, marker.Key, StructureLabels.BrushFor(marker.Key),
                    new Point(mx, my), iconSize);
            }

            if (_spawn is { } sp)
            {
                var sx = (sp.X - left) / (double)step;
                var sy = (sp.Z - top) / (double)step;
                if (sx > -12 && sx < width + 12 && sy > -12 && sy < height + 12)
                {
                    var pen = new Pen(Brushes.White, 1.8);
                    dc.DrawLine(pen, new Point(sx - 6, sy), new Point(sx + 6, sy));
                    dc.DrawLine(pen, new Point(sx, sy - 6), new Point(sx, sy + 6));
                    dc.DrawEllipse(null, pen, new Point(sx, sy), 4, 4);
                }
            }
        }

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

        // 拖之前先确保底下有一层预览图；已经有了就别再排 ——
        // 缩放后每张预览也都不便宜，排在拖动路径上会明显掉帧。
        if (_previewBitmap is null)
        {
            QueuePreviewRender();
        }

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
        // 注意：这里**不能**因为 VisualScale != 1 就整体 return。
        // 那会把拖动、PlanTiles、InvalidateVisual 一起挡掉，
        // 表现就是"拖不动、松手才刷新"。缩放过渡只影响悬停换算，下面单独处理。
        var position = e.GetPosition(this);
        var step = RenderStep;

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
                position.X - _dragStart.X, position.Y - _dragStart.Y, (int)Math.Round(step));
            _centerBlockX = centerX;
            _centerBlockZ = centerZ;

            // 关键：拖动时必须立刻重绘。
            // 以前这里只改了中心、只排了"稍后渲染"，画面要等新像素回来才动 ——
            // 于是拖的时候地图不动，松手才跳到新位置。
            // 现在先让旧图跟着指针平移（O(1)），新图随后补上。
            // 拖动中每 120ms 刷新一次预览层：新露出的区域先有粗图，不出现空白/黑边
            if (now - _lastDragPreviewTicks > 120)
            {
                _lastDragPreviewTicks = now;
                QueuePreviewRender();
            }

            InvalidateVisual();
            ScheduleRenderAfterGesture();
            return;
        }

        var left = _centerBlockX - (int)(ActualWidth / 2 * step);
        var top = _centerBlockZ - (int)(ActualHeight / 2 * step);
        var blockX = left + (int)(position.X * step);
        var blockZ = top + (int)(position.Y * step);

        var nativeId = CubiomesNative.Available
            ? CubiomesNative.BiomeAt(_seed, EngineVersionIndex, _dimension, (int)Math.Round(step), blockX, 63, blockZ)
            : -1;

        // 命中附近的结构（12px 内取最近的一个）
        string? nearStructure = null;
        if (_showStructures)
        {
            var bestDistance = 12.0;
            foreach (var marker in _structures)
            {
                if (!StructureVisible(marker.Key)) continue;
                var mx = (marker.BlockX - left) / (double)step;
                var my = (marker.BlockZ - top) / (double)step;
                var distance = Math.Sqrt((mx - position.X) * (mx - position.X) + (my - position.Y) * (my - position.Y));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearStructure = marker.Label;
                }
            }
        }

        _hoverStructure = nearStructure;

        HoverChanged?.Invoke(this, new BiomeHoverEventArgs(
            blockX, blockZ,
            nativeId >= 0 ? BiomeCatalog.FromCubiomesName(CubiomesNative.BiomeName(EngineVersionIndex, nativeId)) : null,
            nativeId,
            SimplifiedBiomeMap.Sample(_seed, blockX, blockZ),
            SeedMap.IsSlimeChunk(_seed, blockX >> 4, blockZ >> 4),
            nearStructure));
    }

    /// <summary>惯性：中心按松手速度继续滑行，16ms 衰减 12%（约 140ms 停住），期间持续流式取图。</summary>
    private void OnInertiaTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        var dt = Math.Max(1, now - _lastInertiaTick);
        _lastInertiaTick = now;

        var step = RenderStep;
        _centerBlockX -= (int)Math.Round(_inertiaVx * dt * step);
        _centerBlockZ -= (int)Math.Round(_inertiaVy * dt * step);

        _inertiaVx *= 0.88;
        _inertiaVy *= 0.88;

        ScheduleRenderAfterGesture();
        InvalidateVisual();

        if (Math.Abs(_inertiaVx) < 0.02 && Math.Abs(_inertiaVy) < 0.02)
        {
            _inertiaTimer?.Stop();

            if (_renderPendingAfterGesture) ScheduleRenderAfterGesture();
            else RequestRender();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        if (_renderPendingAfterGesture) ScheduleRenderAfterGesture();

        // 轻点（没有拖动）命中结构图标 → 交给界面定位
        var release = e.GetPosition(this);
        if (Math.Abs(release.X - _dragStart.X) < 4 && Math.Abs(release.Y - _dragStart.Y) < 4)
        {
            var step = RenderStep;
            var left = _centerBlockX - (int)(ActualWidth / 2 * step);
            var top = _centerBlockZ - (int)(ActualHeight / 2 * step);

            StructureMarker? hit = null;
            var best = 11.0;
            foreach (var marker in _structures)
            {
                if (!StructureVisible(marker.Key)) continue;
                var mx = (marker.BlockX - left) / (double)step;
                var my = (marker.BlockZ - top) / (double)step;
                var distance = Math.Sqrt((mx - release.X) * (mx - release.X) + (my - release.Y) * (my - release.Y));
                if (distance < best)
                {
                    best = distance;
                    hit = marker;
                }
            }

            if (hit is not null) StructureClicked?.Invoke(this, hit);
        }

        // 惯性：松手后中心继续按速度滑行一小段（在 140ms 内衰减），期间仍然流式请求瓦片
        _inertiaVx = _velocityX;
        _inertiaVy = _velocityY;
        if (Math.Abs(_inertiaVx) < 0.05 && Math.Abs(_inertiaVy) < 0.05)
        {
            ScheduleRenderAfterGesture();
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
