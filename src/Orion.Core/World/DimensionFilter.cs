namespace Orion.Core.World;

/// <summary>
/// 按维度过滤"定位群系 / 定位结构"的候选列表。
///
/// 为什么放在 Core：这段逻辑之前写在 ViewModel 里，既不好测也真的出过两次错
/// （边枚举边改集合、主世界分支写成"全留"）。挪到 Core 后可以单测钉住。
/// </summary>
public static class DimensionFilter
{
    public const int Overworld = 0;
    public const int Nether = 1;
    public const int End = 2;

    private static readonly string[] NetherBiomeMarkers =
        ["nether", "wastes", "soul", "crimson", "warped", "basalt", "hell"];

    private static readonly string[] EndBiomeMarkers = ["end", "void"];

    /// <summary>cubiomes 的群系英文名是否属于该维度。</summary>
    public static bool IsBiomeInDimension(string? cubiomesName, int dimension)
    {
        if (string.IsNullOrWhiteSpace(cubiomesName)) return false;

        var name = cubiomesName.Trim().ToLowerInvariant();
        var isNether = NetherBiomeMarkers.Any(name.Contains);
        var isEnd = EndBiomeMarkers.Any(name.Contains);

        return dimension switch
        {
            Nether => isNether && !isEnd,
            End => isEnd && !isNether,
            _ => !isNether && !isEnd,
        };
    }

    /// <summary>结构 key 是否属于该维度。</summary>
    public static bool IsStructureInDimension(string? key, int dimension)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        var isNether = key is "fortress" or "bastion" or "ruined_portal_nether";
        var isEnd = key is "end_city" or "end_gateway" or "end_ship";

        return dimension switch
        {
            Nether => isNether,
            End => isEnd,
            _ => !isNether && !isEnd,
        };
    }
}
