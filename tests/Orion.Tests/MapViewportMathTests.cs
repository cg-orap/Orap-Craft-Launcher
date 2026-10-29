using Orion.Core.World;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 视口数学：以鼠标为锚点缩放、拖动位移、松手惯性。
/// 这几条决定了"缩放时脚下的方块是否跑偏"，所以按像素对齐来断言。
/// </summary>
public sealed class MapViewportMathTests
{
    [Fact]
    public void ZoomingKeepsTheBlockUnderTheCursorInPlace()
    {
        const int width = 500, height = 400;
        const int centerX = 1234, centerZ = -987;

        foreach (var anchor in new[] { (10.0, 20.0), (250.0, 200.0), (480.0, 380.0) })
        {
            var before = MapViewportMath.BlockAt(centerX, centerZ, width, height, 16, anchor.Item1, anchor.Item2);

            foreach (var newStep in new[] { 8, 4, 1, 32, 64 })
            {
                var (newCenterX, newCenterZ) = MapViewportMath.AnchoredCenter(
                    centerX, centerZ, width, height, anchor.Item1, anchor.Item2, 16, newStep);

                var after = MapViewportMath.BlockAt(newCenterX, newCenterZ, width, height, newStep, anchor.Item1, anchor.Item2);

                // 允许 1 个方块以内的舍入误差
                Assert.InRange(Math.Abs(after.BlockX - before.BlockX), 0, 1);
                Assert.InRange(Math.Abs(after.BlockZ - before.BlockZ), 0, 1);
            }
        }
    }

    [Fact]
    public void ZoomingInAtCenterKeepsCenter()
    {
        var (cx, cz) = MapViewportMath.AnchoredCenter(0, 0, 500, 400, 250, 200, 16, 4);
        Assert.Equal(0, cx);
        Assert.Equal(0, cz);
    }

    [Fact]
    public void DraggingRightMovesTheViewLeft()
    {
        var (cx, cz) = MapViewportMath.DraggedCenter(1000, 2000, offsetPxX: 40, offsetPxY: -25, blocksPerPixel: 16);
        Assert.Equal(1000 - 640, cx);
        Assert.Equal(2000 + 400, cz);
    }

    [Theory]
    [InlineData(1.0, 90)]
    [InlineData(-1.0, -90)]
    [InlineData(0.0, 0)]
    [InlineData(5.0, 160)]      // 上限 160px，甩得再快也不会飞出去
    [InlineData(-5.0, -160)]
    [InlineData(double.NaN, 0)]
    public void InertiaIsClampedAndDefensive(double velocity, double expected)
    {
        Assert.Equal(expected, MapViewportMath.InertiaOffset(velocity), 3);
    }

    [Fact]
    public void ViewOriginMatchesBlockAtZero()
    {
        var (left, top) = MapViewportMath.ViewOrigin(500, -500, 400, 300, 8);
        Assert.Equal(500 - 200 * 8, left);
        Assert.Equal(-500 - 150 * 8, top);

        var (bx, bz) = MapViewportMath.BlockAt(500, -500, 400, 300, 8, 0, 0);
        Assert.Equal(left, bx);
        Assert.Equal(top, bz);
    }
}
