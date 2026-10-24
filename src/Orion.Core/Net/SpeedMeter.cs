using System.Diagnostics;

namespace Orion.Core.Net;

/// <summary>
/// Sliding-window transfer rate meter. Uses an exponentially weighted average over
/// short samples so that the UI shows a stable number instead of a jittering one.
/// </summary>
public sealed class SpeedMeter
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();

    private long _bytes;
    private long _lastBytes;
    private TimeSpan _lastSample;
    private double _bytesPerSecond;

    public long TotalBytes => Interlocked.Read(ref _bytes);

    public void Add(long delta)
    {
        if (delta <= 0) return;
        Interlocked.Add(ref _bytes, delta);
    }

    public TimeSpan Elapsed => _clock.Elapsed;

    public double BytesPerSecond
    {
        get
        {
            Sample();
            lock (_gate) return _bytesPerSecond;
        }
    }

    public void Reset(long initialBytes = 0)
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _bytes, initialBytes);
            _lastBytes = initialBytes;
            _lastSample = _clock.Elapsed;
            _bytesPerSecond = 0;
        }
    }

    private void Sample()
    {
        lock (_gate)
        {
            var now = _clock.Elapsed;
            var deltaTime = (now - _lastSample).TotalSeconds;
            if (deltaTime < 0.25) return;

            var current = Interlocked.Read(ref _bytes);
            var deltaBytes = current - _lastBytes;
            var instantaneous = deltaBytes / deltaTime;

            // Exponential moving average (alpha = 0.35) smooths bursty CDN responses.
            _bytesPerSecond = _bytesPerSecond <= 0 ? instantaneous : (_bytesPerSecond * 0.65) + (instantaneous * 0.35);
            _lastBytes = current;
            _lastSample = now;
        }
    }

    public static string Humanize(double bytesPerSecond) => bytesPerSecond switch
    {
        < 1024 => $"{bytesPerSecond:0} B/s",
        < 1024 * 1024 => $"{bytesPerSecond / 1024:0.0} KB/s",
        _ => $"{bytesPerSecond / (1024 * 1024):0.00} MB/s",
    };
}
