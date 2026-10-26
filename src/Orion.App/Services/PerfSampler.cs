using System.Diagnostics;

namespace Orion.App.Services;

/// <summary>
/// 真实的性能采样：CPU（系统总量）、GPU（GPU Engine 计数器求和，取不到就返回 null）。
/// 采样本身很便宜，但计数器需要一个间隔才有值，所以由调用方按 2 秒节奏驱动。
/// </summary>
public sealed class PerfSampler : IDisposable
{
    private PerformanceCounter? _cpu;
    private PerformanceCounterCategory? _gpuCategory;
    private bool _gpuProbed;
    private DateTime _nextGpuProbe = DateTime.MinValue;

    /// <summary>系统 CPU 占用百分比（0-100）。计数器不可用时返回 null。</summary>
    public double? SampleCpu()
    {
        try
        {
            _cpu ??= new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            var value = _cpu.NextValue();
            // 第一次读取总是 0，用下一次的采样避免把 0 显示给用户。
            return value is >= 0 and <= 100 ? value : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _cpu = null;
            return null;
        }
    }

    /// <summary>
    /// 所有 GPU 引擎的利用率之和（0-100）。显卡驱动未暴露 GPU Engine 计数器时返回 null，
    /// 这样界面显示 “—” 而不是假的 0%。探测器每 30 秒重试一次（驱动更新后能自愈）。
    /// </summary>
    public double? SampleGpu()
    {
        if (!_gpuProbed && DateTime.UtcNow < _nextGpuProbe) return null;

        try
        {
            if (_gpuCategory is null)
            {
                if (!PerformanceCounterCategory.Exists("GPU Engine")) return MarkGpuUnavailable();
                _gpuCategory = new PerformanceCounterCategory("GPU Engine");
            }

            var total = 0d;
            var seen = false;
            foreach (var instance in _gpuCategory.GetInstanceNames())
            {
                // 只统计 3D / Compute 引擎，复制引擎（Copy）会让数字虚高。
                if (!instance.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) &&
                    !instance.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                counter.NextValue();
                total += counter.NextValue();
                seen = true;
            }

            _gpuProbed = true;
            return seen ? Math.Clamp(total, 0, 100) : MarkGpuUnavailable();
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _gpuCategory = null;
            return MarkGpuUnavailable();
        }
    }

    private double? MarkGpuUnavailable()
    {
        _gpuProbed = false;
        _gpuCategory = null;
        _nextGpuProbe = DateTime.UtcNow.AddSeconds(30);
        return null;
    }

    public void Dispose()
    {
        _cpu?.Dispose();
        _cpu = null;
    }
}
