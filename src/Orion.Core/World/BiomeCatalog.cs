namespace Orion.Core.World;

/// <summary>
/// cubiomes 的群系名（英文 snake_case）→ 中文显示名。
///
/// 原生引擎给出的是 cubiomes/util 里的名字，界面上要显示中文，所以做一层映射；
/// 没收录的名字原样返回，避免"未知"掩盖问题。
/// </summary>
public static class BiomeCatalog
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ocean"] = "海洋",
        ["deep_ocean"] = "深海",
        ["warm_ocean"] = "暖水海洋",
        ["lukewarm_ocean"] = "温水海洋",
        ["deep_lukewarm_ocean"] = "深温水海洋",
        ["cold_ocean"] = "冷水海洋",
        ["deep_cold_ocean"] = "深冷水海洋",
        ["frozen_ocean"] = "冻洋",
        ["deep_frozen_ocean"] = "深冻洋",
        ["river"] = "河流",
        ["frozen_river"] = "冻河",
        ["beach"] = "沙滩",
        ["snowy_beach"] = "雪沙滩",
        ["stony_shore"] = "石岸",
        ["plains"] = "平原",
        ["sunflower_plains"] = "向日葵平原",
        ["forest"] = "森林",
        ["flower_forest"] = "繁花森林",
        ["birch_forest"] = "桦木森林",
        ["old_growth_birch_forest"] = "原始桦木森林",
        ["dark_forest"] = "黑森林",
        ["taiga"] = "针叶林",
        ["snowy_taiga"] = "雪针叶林",
        ["old_growth_pine_taiga"] = "原始松木针叶林",
        ["old_growth_spruce_taiga"] = "原始云杉针叶林",
        ["snowy_plains"] = "雪原",
        ["ice_spikes"] = "冰刺之地",
        ["swamp"] = "沼泽",
        ["mangrove_swamp"] = "红树林沼泽",
        ["jungle"] = "丛林",
        ["sparse_jungle"] = "稀疏丛林",
        ["bamboo_jungle"] = "竹林",
        ["desert"] = "沙漠",
        ["savanna"] = "热带草原",
        ["savanna_plateau"] = "热带高原",
        ["windswept_savanna"] = "风袭热带草原",
        ["badlands"] = "恶地",
        ["eroded_badlands"] = "侵蚀恶地",
        ["wooded_badlands"] = "疏林恶地",
        ["windswept_hills"] = "风袭丘陵",
        ["windswept_gravelly_hills"] = "风袭沙砾丘陵",
        ["windswept_forest"] = "风袭森林",
        ["meadow"] = "草甸",
        ["grove"] = "雪林",
        ["snowy_slopes"] = "积雪山坡",
        ["jagged_peaks"] = "尖峭山峰",
        ["frozen_peaks"] = "冰封山峰",
        ["stony_peaks"] = "裸岩山峰",
        ["mushroom_fields"] = "蘑菇岛",
        ["cherry_grove"] = "樱花树林",
        ["pale_garden"] = "苍白之园",
        ["dripstone_caves"] = "溶洞",
        ["lush_caves"] = "繁茂洞穴",
        ["deep_dark"] = "深暗之域",
        ["the_void"] = "虚空",
        ["nether_wastes"] = "下界荒地",
        ["soul_sand_valley"] = "灵魂沙峡谷",
        ["crimson_forest"] = "绯红森林",
        ["warped_forest"] = "诡异森林",
        ["basalt_deltas"] = "玄武岩三角洲",
        ["the_end"] = "末地",
        ["end_highlands"] = "末地高地",
        ["end_midlands"] = "末地内陆",
        ["small_end_islands"] = "末地小岛",
        ["end_barrens"] = "末地荒地",
    };

    /// <summary>把 cubiomes 的英文名转成中文；未收录则原样返回。</summary>
    public static string? FromCubiomesName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var key = name.Trim();
        // cubiomes 可能给出 "minecraft:plains" 这类带命名空间的名字
        var colon = key.IndexOf(':');
        if (colon >= 0) key = key[(colon + 1)..];

        return Names.TryGetValue(key, out var chinese) ? chinese : key;
    }
}
