using Orion.Core.Instances;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 游玩记录：启动次数、累计时长、平均时长、异常退出，存到 playstats.json。
/// 规则：删版本不删统计（历史仍可见）；时长为 0 的会话只记启动次数。
/// </summary>
public sealed class PlayStatsTests : IDisposable
{
    private readonly string _path;
    private readonly PlayStatsStore _store;

    public PlayStatsTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "orap-stats", Guid.NewGuid().ToString("N"), "playstats.json");
        _store = new PlayStatsStore(_path);
    }

    public void Dispose()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void FirstLaunchCreatesARecord()
    {
        var stats = _store.RecordLaunch("atm10", "All the Mods 10");

        Assert.Equal(1, stats.Launches);
        Assert.Equal("All the Mods 10", stats.Name);
        Assert.NotNull(stats.FirstPlayedUtc);
        Assert.NotNull(stats.LastPlayedUtc);
        Assert.Equal(0, stats.TotalSeconds);
    }

    [Fact]
    public void LaunchesAccumulate()
    {
        _store.RecordLaunch("atm10", "ATM10");
        _store.RecordLaunch("atm10", "ATM10");
        var stats = _store.RecordLaunch("atm10", "ATM10");

        Assert.Equal(3, stats.Launches);
    }

    [Fact]
    public void SessionsAccumulateSeconds()
    {
        _store.RecordLaunch("bmc4", "BMC4");
        _store.RecordLaunch("bmc4", "BMC4");
        _store.RecordSession("bmc4", "BMC4", TimeSpan.FromMinutes(30));
        var stats = _store.RecordSession("bmc4", "BMC4", TimeSpan.FromMinutes(15));

        Assert.Equal(2700, stats.TotalSeconds);
        Assert.Equal("45 分钟", stats.TotalPlayText);
        Assert.Equal("22 分钟", stats.AverageSessionText);
    }

    [Fact]
    public void AbnormalExitIsCounted()
    {
        _store.RecordLaunch("x", "X");
        _store.RecordSession("x", "X", TimeSpan.FromMinutes(2), exitCode: 1);

        Assert.Equal(1, _store.For("x")!.AbnormalExits);
    }

    [Fact]
    public void DataSurvivesReload()
    {
        _store.RecordLaunch("keep", "Keep");
        _store.RecordSession("keep", "Keep", TimeSpan.FromHours(2));

        var reopened = new PlayStatsStore(_path);
        var stats = reopened.For("keep");

        Assert.NotNull(stats);
        Assert.Equal(1, stats!.Launches);
        Assert.Equal(7200, stats.TotalSeconds);
        Assert.Equal("2 小时", stats.TotalPlayText);
    }

    [Fact]
    public void RecordsAreSortedByMostRecent()
    {
        _store.RecordLaunch("old", "Old", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _store.RecordLaunch("new", "New", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var all = _store.All();

        Assert.Equal("new", all[0].InstanceId);
        Assert.Equal("old", all[1].InstanceId);
    }

    [Fact]
    public void ClearRemovesEverything()
    {
        _store.RecordLaunch("a", "A");
        _store.Clear();

        Assert.Empty(_store.All());
    }

    [Fact]
    public void RemoveDropsOneInstanceOnly()
    {
        _store.RecordLaunch("a", "A");
        _store.RecordLaunch("b", "B");

        _store.Remove("a");

        Assert.Null(_store.For("a"));
        Assert.NotNull(_store.For("b"));
    }

    [Theory]
    [InlineData(0, "0 分钟")]
    [InlineData(59, "1 分钟")]
    [InlineData(600, "10 分钟")]
    [InlineData(3600, "1 小时")]
    [InlineData(3900, "1 小时 5 分钟")]
    public void DurationFormattingIsReadable(int seconds, string expected)
    {
        Assert.Equal(expected, InstancePlayStats.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void MissingFileYieldsEmptyStats()
    {
        var fresh = new PlayStatsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nope.json"));

        Assert.Empty(fresh.All());
        Assert.Null(fresh.For("anything"));
    }
}
