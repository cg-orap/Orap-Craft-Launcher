namespace Orion.Core.World;

/// <summary>
/// 种子地图视口的纯计算部分（以鼠标为锚点缩放、拖动位移、惯性滑行）。
///
/// 抽出来是为了能单测：WPF 里的鼠标事件不好自动化，但这几段数学决定了"缩放时脚下的方块
/// 不会跑偏""松手后滑行多远"，错一点用户就会觉得别扭。
/// </summary>
public static class MapViewportMath
{
    /// <summary>
    /// 视口左上角对应的方块坐标。
    /// </summary>
    public static (int BlockX, int BlockZ) ViewOrigin(int centerX, int centerZ, int pixelWidth, int pixelHeight, int blocksPerPixel)
    {
        var halfW = pixelWidth / 2 * blocksPerPixel;
        var halfH = pixelHeight / 2 * blocksPerPixel;
        return (centerX - halfW, centerZ - halfH);
    }

    /// <summary>屏幕上某点对应的方块坐标。</summary>
    public static (int BlockX, int BlockZ) BlockAt(int centerX, int centerZ, int pixelWidth, int pixelHeight,
        int blocksPerPixel, double screenX, double screenY)
    {
        var (left, top) = ViewOrigin(centerX, centerZ, pixelWidth, pixelHeight, blocksPerPixel);
        return (left + (int)Math.Round(screenX * blocksPerPixel),
                top + (int)Math.Round(screenY * blocksPerPixel));
    }

    /// <summary>
    /// 以鼠标位置为锚点缩放：锚点下方的方块在缩放前后必须落在同一个屏幕位置。
    /// 返回新的中心方块坐标。
    /// </summary>
    public static (int CenterX, int CenterZ) AnchoredCenter(int centerX, int centerZ,
        int pixelWidth, int pixelHeight, double anchorX, double anchorY,
        int oldBlocksPerPixel, int newBlocksPerPixel)
    {
        var (anchorBlockX, anchorBlockZ) = BlockAt(centerX, centerZ, pixelWidth, pixelHeight,
            oldBlocksPerPixel, anchorX, anchorY);

        // 反解：anchorBlock = newCenter - width/2*step + anchorX*step
        var newCenterX = anchorBlockX + (int)Math.Round((pixelWidth / 2.0 - anchorX) * newBlocksPerPixel);
        var newCenterZ = anchorBlockZ + (int)Math.Round((pixelHeight / 2.0 - anchorY) * newBlocksPerPixel);
        return (newCenterX, newCenterZ);
    }

    /// <summary>拖动 offsetPx 像素后新的中心（向右拖 → 中心左移）。</summary>
    public static (int CenterX, int CenterZ) DraggedCenter(int startCenterX, int startCenterZ,
        double offsetPxX, double offsetPxY, int blocksPerPixel) =>
        (startCenterX - (int)Math.Round(offsetPxX * blocksPerPixel),
         startCenterZ - (int)Math.Round(offsetPxY * blocksPerPixel));

    /// <summary>
    /// 松手惯性：按速度再滑一段（默认 90ms），并限制最大 160px，避免"甩飞"。
    /// </summary>
    public static double InertiaOffset(double velocityPxPerMs, int maxPixels = 160, double milliseconds = 90.0)
    {
        if (double.IsNaN(velocityPxPerMs) || double.IsInfinity(velocityPxPerMs)) return 0;
        return Math.Clamp(velocityPxPerMs * milliseconds, -maxPixels, maxPixels);
    }
    /// <summary>主世界坐标 → 下界坐标（1:8，向下取整，与游戏内传送门换算一致）。</summary>
    public static (int X, int Z) ToNether(int overworldX, int overworldZ) =>
        (FloorDiv8(overworldX), FloorDiv8(overworldZ));

    /// <summary>下界坐标 → 主世界坐标（×8）。</summary>
    public static (int X, int Z) ToOverworld(int netherX, int netherZ) => (netherX * 8, netherZ * 8);

    private static int FloorDiv8(int value) => value >= 0 ? value / 8 : (value - 7) / 8;
}
