using System.Windows.Media;

namespace Orion.App.Controls;

/// <summary>
/// 结构标记的中文名与配色（key 与 C 侧 <c>orap_cubiomes.c</c> 的结构表一致）。
/// </summary>
public static class StructureLabels
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["village"] = "村庄",
        ["outpost"] = "掠夺者前哨站",
        ["monument"] = "海底神殿",
        ["mansion"] = "林地府邸",
        ["desert_pyramid"] = "沙漠神殿",
        ["jungle_temple"] = "丛林神庙",
        ["swamp_hut"] = "沼泽小屋",
        ["igloo"] = "雪屋",
        ["ocean_ruin"] = "海底废墟",
        ["shipwreck"] = "沉船",
        ["treasure"] = "埋藏的宝藏",
        ["ruined_portal"] = "废弃传送门",
        ["ancient_city"] = "远古城市",
        ["trail_ruins"] = "古迹废墟",
        ["trial_chambers"] = "试炼密室",
        ["fortress"] = "下界要塞",
        ["bastion"] = "堡垒遗迹",
        ["ruined_portal_nether"] = "下界废弃传送门",
        ["end_city"] = "末地城",
        ["end_gateway"] = "末地折跃门",
        ["stronghold"] = "要塞",
        ["spawn"] = "出生点",
    };

    private static readonly Dictionary<string, Color> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["village"] = Color.FromRgb(0xF2, 0xD1, 0x6B),
        ["outpost"] = Color.FromRgb(0xD1, 0x6B, 0x5A),
        ["monument"] = Color.FromRgb(0x67, 0xC7, 0xD8),
        ["mansion"] = Color.FromRgb(0x9B, 0x6B, 0xC7),
        ["desert_pyramid"] = Color.FromRgb(0xE0, 0xB5, 0x6B),
        ["jungle_temple"] = Color.FromRgb(0x6B, 0xC7, 0x7A),
        ["swamp_hut"] = Color.FromRgb(0x5E, 0x8A, 0x5A),
        ["igloo"] = Color.FromRgb(0xD8, 0xEC, 0xF5),
        ["ocean_ruin"] = Color.FromRgb(0x4A, 0x8A, 0xC7),
        ["shipwreck"] = Color.FromRgb(0x9A, 0x7B, 0x5A),
        ["treasure"] = Color.FromRgb(0xF5, 0xB6, 0x4A),
        ["ruined_portal"] = Color.FromRgb(0xB0, 0x7A, 0xD8),
        ["ancient_city"] = Color.FromRgb(0x2E, 0x2E, 0x3A),
        ["trail_ruins"] = Color.FromRgb(0xC7, 0xA8, 0x7A),
        ["trial_chambers"] = Color.FromRgb(0x8A, 0x6B, 0x4A),
        ["fortress"] = Color.FromRgb(0xE0, 0x6B, 0x4A),
        ["bastion"] = Color.FromRgb(0x8A, 0x4A, 0x7A),
        ["ruined_portal_nether"] = Color.FromRgb(0xB0, 0x7A, 0xD8),
        ["end_city"] = Color.FromRgb(0xE8, 0xE0, 0xB0),
        ["end_gateway"] = Color.FromRgb(0xC7, 0x8A, 0xE0),
        ["stronghold"] = Color.FromRgb(0xFF, 0xFF, 0xFF),
    };

    private static readonly Dictionary<string, Brush> Brushes = new(StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string key) => Names.TryGetValue(key, out var name) ? name : key;

    public static Brush BrushFor(string key)
    {
        if (Brushes.TryGetValue(key, out var brush)) return brush;

        var color = Colors.TryGetValue(key, out var known) ? known : Color.FromRgb(0xB8, 0xBC, 0xC4);
        brush = new SolidColorBrush(color);
        brush.Freeze();
        Brushes[key] = brush;
        return brush;
    }
}
