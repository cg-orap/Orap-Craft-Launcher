using System.Windows.Media.Imaging;
using Orion.Core.World;

namespace Orion.App.Controls;

/// <summary>
/// 瓦片层：像 cubiomes-viewer / Axolotl 那样按 256×256 的瓦片渲染、
/// 缓存、按需补块，而不是每次平移都重画整屏。
///
/// 为什么能对齐：原生接口是"中心 + 采样比例 + 像素数"，
/// 区域起点算出来是 `center - cellsX/2*scale`，一定是 scale 的整数倍。
/// 只要瓦片跨度的方块数是 scale 的整数倍，相邻瓦片就严丝合缝。
///
/// 缩放档位里 3/6/12/24/48 无法整除原生采样比例，所以它们用
/// "最近的、能整除的档位"渲染，再按比例放大绘制（画面上等效，接缝不受影响）。
/// </summary>
public sealed class BiomeTileLayer
{
    /// <summary>每块瓦片的像素边长。</summary>
    public const int TilePixels = 256;

    /// <summary>缓存上限（块）。48 × 256² × 4B ≈ 12 MB。</summary>
    private const int MaxTiles = 48;

    private sealed class Tile
    {
        public required BitmapSource Bitmap { get; init; }
        public required int RenderStep { get; init; }
        public required int WorldLeft { get; init; }
        public required int WorldTop { get; init; }
        public required int SpanBlocks { get; init; }
        public long LastUsed { get; set; }
    }

    private readonly Dictionary<long, Tile> _tiles = [];
    private readonly HashSet<long> _inFlight = [];
    private readonly Queue<(int RenderStep, int TileX, int TileZ)> _queue = [];
    private readonly object _gate = new();

    /// <summary>渲染一块瓦片（在后台线程调用）。返回 null 表示失败。</summary>
    public Func<TileRequest, Task<byte[]?>>? Renderer { get; set; }

    /// <summary>有瓦片到达时通知界面重绘。</summary>
    public Action? Updated { get; set; }

    public readonly record struct TileRequest(
        long Seed, int VersionIndex, int Dimension, int RenderStep, int PixPerCell,
        int CenterX, int CenterZ, int Pixels);

    /// <summary>把缩放档位映射到"原生能整除的渲染档位"。</summary>
    public static (int RenderStep, int RenderScale, int PixPerCell) Resolve(int step) => step switch
    {
        <= 1 => (1, 1, 1),
        <= 4 => (4, 4, 1),
        <= 16 => (16, 16, 1),
        _ => (64, 64, 1),
    };

    /// <summary>当前档位下，一块瓦片覆盖多少方块。</summary>
    public static int TileSpan(int renderStep) => TilePixels * renderStep;

    private static long Key(int renderStep, int tileX, int tileZ) =>
        ((long)renderStep << 46) ^ ((long)(tileX & 0x1FFFFF) << 23) ^ (long)(tileZ & 0x1FFFFF);

    /// <summary>排入"当前视口需要的瓦片"，多余/过期的直接丢弃。</summary>
    public void Plan(int step, double viewLeft, double viewTop, double viewWidth, double viewHeight,
        int maxConcurrent = 3)
    {
        var (renderStep, _, _) = Resolve(step);
        var span = TileSpan(renderStep);

        var minTileX = (int)Math.Floor(viewLeft / span);
        var maxTileX = (int)Math.Floor((viewLeft + viewWidth) / span);
        var minTileZ = (int)Math.Floor(viewTop / span);
        var maxTileZ = (int)Math.Floor((viewTop + viewHeight) / span);

        var centerX = viewLeft + viewWidth / 2;
        var centerZ = viewTop + viewHeight / 2;

        var wanted = new List<(int TileX, int TileZ, double Distance)>();

        for (var tz = minTileZ - 1; tz <= maxTileZ + 1; tz++)
        {
            for (var tx = minTileX - 1; tx <= maxTileX + 1; tx++)
            {
                var key = Key(renderStep, tx, tz);
                if (_tiles.ContainsKey(key) || _inFlight.Contains(key)) continue;

                var cx = (tx + 0.5) * span;
                var cz = (tz + 0.5) * span;
                wanted.Add((tx, tz, (cx - centerX) * (cx - centerX) + (cz - centerZ) * (cz - centerZ)));
            }
        }

        lock (_gate)
        {
            _queue.Clear();
            foreach (var (tx, tz, _) in wanted.OrderBy(w => w.Distance)) _queue.Enqueue((renderStep, tx, tz));
        }

        Pump(maxConcurrent);
    }

    private void Pump(int maxConcurrent = 3)
    {
        while (true)
        {
            (int RenderStep, int TileX, int TileZ) job;

            lock (_gate)
            {
                if (_inFlight.Count >= maxConcurrent || _queue.Count == 0) return;
                job = _queue.Dequeue();
                _inFlight.Add(Key(job.RenderStep, job.TileX, job.TileZ));
            }

            _ = RenderAsync(job);
        }
    }

    private async Task RenderAsync((int RenderStep, int TileX, int TileZ) job)
    {
        var (renderStep, tileX, tileZ) = job;
        var span = TileSpan(renderStep);

        try
        {
            // 瓦片中心 = 瓦片原点 + 半个跨度；原生会据此反推区域起点（对齐到 scale）
            var factory = RequestFactory;
            if (factory is null) return;

            var request = factory(renderStep, tileX * span + span / 2, tileZ * span + span / 2);

            var renderer = Renderer;
            if (renderer is null) return;

            var pixels = await renderer(request).ConfigureAwait(false);
            if (pixels is null) return;

            var bitmap = BitmapSource.Create(TilePixels, TilePixels, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, TilePixels * 4);
            bitmap.Freeze();

            lock (_gate)
            {
                _tiles[Key(renderStep, tileX, tileZ)] = new Tile
                {
                    Bitmap = bitmap,
                    RenderStep = renderStep,
                    WorldLeft = tileX * span,
                    WorldTop = tileZ * span,
                    SpanBlocks = span,
                    LastUsed = Environment.TickCount64,
                };

                Trim();
            }

            Updated?.Invoke();
        }
        finally
        {
            lock (_gate) _inFlight.Remove(Key(renderStep, tileX, tileZ));
            Pump();
        }
    }

    private void Trim()
    {
        if (_tiles.Count <= MaxTiles) return;

        foreach (var key in _tiles.OrderBy(kv => kv.Value.LastUsed).Select(kv => kv.Key)
                     .Take(_tiles.Count - MaxTiles).ToList())
        {
            _tiles.Remove(key);
        }
    }

    /// <summary>由画布注入：给定 (renderStep, centerX, centerZ) 生成一个原生渲染请求。</summary>
    public Func<int, int, int, TileRequest>? RequestFactory { get; set; }

    /// <summary>
    /// 取出当前视口内所有可画的瓦片（世界坐标 + 位图）。
    ///
    /// **渐进式 LOD**：除了当前档位的瓦片，也把其它档位的瓦片一起返回，
    /// 只是排在后面（后画的在上层）。这样刚切换缩放、新档位还没补齐时，
    /// 画面用的是"旧档位放大后的图"，而不是一片空白 —— 缩放时不会闪、不会卡。
    /// </summary>
    public IEnumerable<(BitmapSource Bitmap, double WorldLeft, double WorldTop, double SpanBlocks)> Visible(
        int step, double viewLeft, double viewTop, double viewWidth, double viewHeight)
    {
        var (renderStep, _, _) = Resolve(step);
        var now = Environment.TickCount64;

        var current = new List<(BitmapSource, double, double, double)>();
        var fallback = new List<(BitmapSource, double, double, double)>();

        // 必须在锁内做快照：后台渲染线程会往 _tiles 里 Add/Remove，
        // 直接在 OnRender 里遍历 Values 会抛 "Collection was modified"，
        // 而这个异常抛在 WPF 渲染回调里 → 全局未处理异常弹窗。
        List<Tile> snapshot;

        lock (_gate) snapshot = _tiles.Values.ToList();

        foreach (var tile in snapshot)
        {
            if (tile.WorldLeft + tile.SpanBlocks <= viewLeft) continue;
            if (tile.WorldTop + tile.SpanBlocks <= viewTop) continue;
            if (tile.WorldLeft >= viewLeft + viewWidth) continue;
            if (tile.WorldTop >= viewTop + viewHeight) continue;

            tile.LastUsed = now;

            var entry = (tile.Bitmap, (double)tile.WorldLeft, (double)tile.WorldTop, (double)tile.SpanBlocks);

            if (tile.RenderStep == renderStep) current.Add(entry);
            else fallback.Add(entry);
        }

        // 旧档位的图先画（当底），当前档位后画（盖在上面）
        fallback.AddRange(current);
        return fallback;
    }

    /// <summary>换种子/换维度/改样式时清空。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _tiles.Clear();
            _queue.Clear();
            _inFlight.Clear();
        }
    }

    /// <summary>已经有多少块瓦片可用（自检/调试用）。</summary>
    public int Count
    {
        get { lock (_gate) return _tiles.Count; }
    }
}
