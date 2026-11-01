using System.Text.Json;
using System.Text.Json.Serialization;
using Orion.Core.Diagnostics;

namespace Orion.Core.Instances;

/// <summary>一个版本的游玩统计。</summary>
public sealed class InstancePlayStats
{
    [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = "";

    /// <summary>最后一次记录时的版本名（实例被删了也还能看懂）。</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>启动次数（点「启动游戏」并真的把进程拉起来的次数）。</summary>
    [JsonPropertyName("launches")] public int Launches { get; set; }

    /// <summary>累计游玩秒数（从进程启动到退出的墙钟时间之和）。</summary>
    [JsonPropertyName("totalSeconds")] public long TotalSeconds { get; set; }

    [JsonPropertyName("firstPlayedUtc")] public DateTime? FirstPlayedUtc { get; set; }

    [JsonPropertyName("lastPlayedUtc")] public DateTime? LastPlayedUtc { get; set; }

    /// <summary>非 0 退出码的次数（崩溃/异常退出）。</summary>
    [JsonPropertyName("abnormalExits")] public int AbnormalExits { get; set; }

    [JsonIgnore] public TimeSpan TotalPlayTime => TimeSpan.FromSeconds(TotalSeconds);

    [JsonIgnore] public TimeSpan AverageSession => Launches <= 0
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds(TotalSeconds / (double)Launches);

    /// <summary>"3 小时 12 分钟" 这样的可读时长。</summary>
    [JsonIgnore] public string TotalPlayText => FormatDuration(TotalPlayTime);

    [JsonIgnore] public string AverageSessionText => FormatDuration(AverageSession);

    [JsonIgnore] public string LastPlayedText => LastPlayedUtc is { } t
        ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "从未";

    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalSeconds < 1) return "0 分钟";

        if (span.TotalHours >= 1)
        {
            var hours = (int)span.TotalHours;
            var minutes = span.Minutes;
            return minutes == 0 ? $"{hours} 小时" : $"{hours} 小时 {minutes} 分钟";
        }

        return $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} 分钟";
    }
}

/// <summary>
/// 游玩记录：按实例统计启动次数与总时长，存到数据目录下的 <c>playstats.json</c>。
///
/// 为什么单独一个文件：不污染 instance.json，删版本也不会连带删统计（历史还能看到）。
/// </summary>
public sealed class PlayStatsStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _gate = new();
    private Dictionary<string, InstancePlayStats>? _cache;

    /// <summary>所有记录（按最近游玩倒序）。</summary>
    public IReadOnlyList<InstancePlayStats> All()
    {
        lock (_gate)
        {
            return [.. Load().Values
                .OrderByDescending(s => s.LastPlayedUtc ?? DateTime.MinValue)];
        }
    }

    public InstancePlayStats? For(string instanceId)
    {
        lock (_gate)
        {
            return Load().TryGetValue(instanceId, out var stats) ? stats : null;
        }
    }

    /// <summary>记一次启动。</summary>
    public InstancePlayStats RecordLaunch(string instanceId, string name, DateTime? when = null)
    {
        lock (_gate)
        {
            var stats = Ensure(instanceId, name);
            var at = when ?? DateTime.UtcNow;

            stats.Launches++;
            stats.FirstPlayedUtc ??= at;
            stats.LastPlayedUtc = at;

            Save();
            return stats;
        }
    }

    /// <summary>记一次游玩结束（时长 + 是否异常退出）。</summary>
    public InstancePlayStats RecordSession(string instanceId, string name, TimeSpan duration,
        int exitCode = 0, DateTime? when = null)
    {
        lock (_gate)
        {
            var stats = Ensure(instanceId, name);
            var at = when ?? DateTime.UtcNow;

            // 0 秒的会话（刚点开就关）也算一次，但不加时长
            stats.TotalSeconds += Math.Max(0, (long)duration.TotalSeconds);
            stats.LastPlayedUtc = at;
            if (exitCode != 0) stats.AbnormalExits++;

            Save();
            return stats;
        }
    }

    /// <summary>清空全部记录。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _cache = [];
            Save();
        }
    }

    /// <summary>删掉某个实例的记录（删版本时可选调用）。</summary>
    public void Remove(string instanceId)
    {
        lock (_gate)
        {
            if (Load().Remove(instanceId)) Save();
        }
    }

    private InstancePlayStats Ensure(string instanceId, string name)
    {
        var all = Load();
        if (!all.TryGetValue(instanceId, out var stats))
        {
            stats = new InstancePlayStats { InstanceId = instanceId };
            all[instanceId] = stats;
        }

        if (!string.IsNullOrWhiteSpace(name)) stats.Name = name;
        return stats;
    }

    private Dictionary<string, InstancePlayStats> Load()
    {
        if (_cache is not null) return _cache;

        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var list = JsonSerializer.Deserialize<List<InstancePlayStats>>(json, JsonOptions) ?? [];

                _cache = list
                    .Where(s => !string.IsNullOrWhiteSpace(s.InstanceId))
                    .GroupBy(s => s.InstanceId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                return _cache;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warn("stats", "load-failed", ("error", ex.Message));
        }

        _cache = new Dictionary<string, InstancePlayStats>(StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(Load().Values.ToList(), JsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("stats", "save-failed", ("error", ex.Message));
        }
    }
}
