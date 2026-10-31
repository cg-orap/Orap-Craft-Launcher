using Orion.Core.Net;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 下载速度表：界面上那行"正在下载：x MB/s"就是它算的。
/// </summary>
public sealed class TransferMeterTests
{
    [Fact]
    public void NothingReportedMeansNoSpeedAndNoText()
    {
        TransferMeter.Reset();

        Assert.Equal(0, TransferMeter.BytesPerSecond);
        Assert.Equal("", TransferMeter.FormatSpeed());
    }

    [Fact]
    public void ReportedBytesProduceASpeedAndReadableText()
    {
        TransferMeter.Reset();

        // 模拟 1 MB 的下载
        for (var i = 0; i < 16; i++) TransferMeter.Report(64 * 1024);

        Assert.True(TransferMeter.TotalBytes >= 1024 * 1024);
        Assert.True(TransferMeter.BytesPerSecond > 0);

        var text = TransferMeter.FormatSpeed();
        Assert.Contains("/s", text);

        TransferMeter.Reset();
    }

    [Fact]
    public void ResetClearsTheSpeed()
    {
        TransferMeter.Report(1024 * 1024);
        Assert.NotEqual("", TransferMeter.FormatSpeed());

        TransferMeter.Reset();
        Assert.Equal("", TransferMeter.FormatSpeed());
    }
}
