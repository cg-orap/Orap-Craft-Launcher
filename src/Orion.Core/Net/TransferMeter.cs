namespace Orion.Core.Net;

/// <summary>
/// 全局下载速率表。
///
/// 各个下载路径（整合包文件、CurseForge 模组、游戏文件）只要在拷贝字节时调一下 <see cref="Report"/>，
/// 界面就能显示"当前下载速度"，不需要每条链路各自实现测速。
///
/// 算法是"从第一次登记到现在的平均速度"，每满 1 秒重置计量窗口，空闲超过 2 秒归零：
/// 数字不会乱跳，也不依赖采样精度——对"下载中心那行字"来说足够了。
/// </summary>
public static class TransferMeter
{
    private const int IdleResetMs = 2000;

    private static readonly Lock Gate = new();

    private static long _totalBytes;
    private static long _windowBytes;
    private static long _firstTick;
    private static long _lastTick;
    private static double _rate;

    /// <summary>累计下载了多少字节（进程生命周期内累加，只增不减）。</summary>
    public static long TotalBytes
    {
        get { lock (Gate) return _totalBytes; }
    }

    /// <summary>登记一段新下载的字节。</summary>
    public static void Report(int bytes)
    {
        if (bytes <= 0) return;

        lock (Gate)
        {
            var now = Environment.TickCount64;

            _totalBytes += bytes;
            _windowBytes += bytes;

            if (_firstTick == 0) _firstTick = now;
            _lastTick = now;

            var elapsed = (now - _firstTick) / 1000.0;
            if (elapsed < 0.02) elapsed = 0.02;   // 起手极快时给一个瞬时值，避免显示 0

            var instant = _windowBytes / elapsed;
            _rate = _rate <= 0 ? instant : (_rate * 0.5) + (instant * 0.5);

            // 每秒重置一次窗口，速度能跟随当前带宽变化
            if (elapsed >= 1.0)
            {
                _windowBytes = 0;
                _firstTick = now;
            }
        }
    }

    /// <summary>当前速度（字节/秒）；空闲超过 2 秒返回 0。</summary>
    public static double BytesPerSecond
    {
        get
        {
            lock (Gate)
            {
                if (_lastTick == 0) return 0;
                if (Environment.TickCount64 - _lastTick > IdleResetMs) return 0;

                return _rate;
            }
        }
    }

    /// <summary>速度文本，例如 "3.4 MB/s"；没在下载就是空字符串。</summary>
    public static string FormatSpeed()
    {
        var speed = BytesPerSecond;
        if (speed < 1024) return "";

        return speed >= 1024 * 1024
            ? $"{speed / 1024.0 / 1024.0:0.0} MB/s"
            : $"{speed / 1024.0:0} KB/s";
    }

    /// <summary>清零（测试或切换任务时用）。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _windowBytes = 0;
            _firstTick = 0;
            _lastTick = 0;
            _rate = 0;
        }
    }
}
