namespace Orion.Core.Mods;

/// <summary>
/// 内容分类的 Slug → 中文名映射。
///
/// Modrinth 的分类是英文 slug（adventure / technology / optimization…），
/// CurseForge 用中文或英文的分类名。界面统一显示中文，筛选时再翻回各平台需要的值。
/// </summary>
public static class ModCategories
{
    /// <summary>中文名 → Modrinth slug。</summary>
    private static readonly (string Slug, string Name)[] All =
    [
        // 整合包常见的
        ("adventure", "冒险"),
        ("combat", "战斗"),
        ("optimization", "优化"),
        ("technology", "科技"),
        ("magic", "魔法"),
        ("quests", "任务"),
        ("worldgen", "地形生成"),
        ("storage", "存储"),
        ("decoration", "装饰"),
        ("mobs", "生物"),
        ("library", "前置库"),
        ("food", "食物"),
        ("equipment", "装备"),
        ("game-mechanics", "机制改动"),
        ("utility", "实用工具"),
        ("multiplayer", "多人"),
        ("minigame", "小游戏"),
        ("challenging", "高难度"),
        ("kitchen-sink", "大杂烩"),
        ("lightweight", "轻量"),
        ("themed", "主题"),

        // 资源包/光影常见的
        ("tweaks", "微调"),
        ("fonts", "字体"),
        ("vanilla-like", "原版风格"),
        ("realistic", "写实"),
        ("semi-realistic", "半写实"),
        ("cartoon", "卡通"),
        ("fantasy", "奇幻"),
        ("simplistic", "简洁"),
        ("audio", "音效"),
        ("cursed", "搞怪"),
        ("blocks", "方块"),
        ("entities", "实体"),
        ("gui", "界面"),
        ("items", "物品"),
        ("environment", "环境"),
        ("core-shaders", "核心光影"),
        ("medium", "中等重量"),
        ("heavy", "高负载"),
        ("potato", "低配可用"),
        ("low", "低配"),

        // 加载器/其它
        ("forge", "Forge"),
        ("fabric", "Fabric"),
        ("neoforge", "NeoForge"),
        ("quilt", "Quilt"),
    ];

    private static readonly Dictionary<string, string> SlugToName =
        All.ToDictionary(x => x.Slug, x => x.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> NameToSlug =
        All.ToDictionary(x => x.Name, x => x.Slug, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// CurseForge 分类名的中文翻译（按 CF 的英文名归一化后匹配）。
    /// 翻不出来就保留原名 —— 显示英文也比"选了没反应"好。
    /// </summary>
    private static readonly Dictionary<string, string> CurseForgeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Adventure"] = "冒险",
        ["API and Library"] = "前置库",
        ["Armor, Tools, and Weapons"] = "装备",
        ["Armor Tools and Weapons"] = "装备",
        ["Cosmetic"] = "外观装饰",
        ["Decoration"] = "装饰",
        ["Food"] = "食物",
        ["Magic"] = "魔法",
        ["Technology"] = "科技",
        ["Storage"] = "存储",
        ["World Gen"] = "地形生成",
        ["World Generation"] = "地形生成",
        ["Mobs"] = "生物",
        ["Utility & QoL"] = "实用工具",
        ["Utility and QoL"] = "实用工具",
        ["Game Mechanics"] = "机制改动",
        ["Optimization"] = "优化",
        ["Performance"] = "优化",
        ["Server Utility"] = "服务器工具",
        ["Transport"] = "交通",
        ["Player Transport"] = "玩家交通",
        ["Automation"] = "自动化",
        ["Energy"] = "能源",
        ["Generators"] = "发电机",
        ["Redstone"] = "红石",
        ["Processing"] = "处理",
        ["Map and Information"] = "地图与信息",
        ["Miscellaneous"] = "杂项",
        ["Education"] = "教学",
        ["Economy"] = "经济",
        ["Create"] = "机械动力",
        ["Twitch Integration"] = "Twitch 集成",
        ["Font Packs"] = "字体",
        ["Themed"] = "主题",
        ["Tweaks"] = "微调",
        ["Realistic"] = "写实",
        ["Semi-realistic"] = "半写实",
        ["Fantasy"] = "奇幻",
        ["Cartoon"] = "卡通",
        ["Simplistic"] = "简洁",
        ["Vanilla-like"] = "原版风格",
        ["Modded"] = "模组向",
        ["Questing"] = "任务",
        ["Quests"] = "任务",
        ["Hardcore"] = "硬核",
        ["Challenge"] = "挑战",
        ["Extra Large"] = "超大",
        ["Large"] = "大型",
        ["Medium"] = "中型",
        ["Small"] = "小型",
        ["Lightweight"] = "轻量",
        ["Multiplayer"] = "多人",
        ["Sci-Fi"] = "科幻",
        ["Steampunk"] = "蒸汽朋克",
        ["Medieval"] = "中世纪",
        ["Modern"] = "现代",
        ["Core Shaders"] = "核心光影",
        ["Shaders"] = "光影",
        ["Potato"] = "低配可用",
        ["Low"] = "低配",
    };

    /// <summary>CurseForge 分类名 → 中文；翻不出来原样返回。</summary>
    public static string CurseForgeDisplayName(string? curseForgeName)
    {
        if (string.IsNullOrWhiteSpace(curseForgeName)) return "";

        var key = curseForgeName.Trim();
        return CurseForgeNames.TryGetValue(key, out var chinese) ? chinese : key;
    }

    /// <summary>某个分类的中文名；查不到时把 slug 首字母大写，避免显示成英文小写。</summary>
    public static string DisplayName(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return "";

        var key = slug.Trim();
        if (SlugToName.TryGetValue(key, out var name)) return name;

        // "game-mechanics" → "Game Mechanics"
        var words = key.Replace('-', ' ').Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return string.Join(" ", words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>中文名 → slug（用户在界面上选的是中文）。</summary>
    public static string? SlugOf(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        var key = displayName.Trim();
        if (NameToSlug.TryGetValue(key, out var slug)) return slug;
        if (SlugToName.ContainsKey(key)) return key.ToLowerInvariant();

        return null;
    }

    /// <summary>把一批 slug 变成中文标签（去重、保序）。</summary>
    public static IReadOnlyList<string> DisplayNames(IEnumerable<string>? slugs, int max = 4)
    {
        if (slugs is null) return [];

        var result = new List<string>();
        foreach (var slug in slugs)
        {
            var name = DisplayName(slug);
            if (name.Length == 0 || result.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

            result.Add(name);
            if (result.Count >= max) break;
        }

        return result;
    }

    /// <summary>
    /// CurseForge 的分类名候选（不同年份/语言下 CF 返回的名字不完全一致，
    /// 这里给出几种常见写法，调用方按顺序匹配）。
    /// </summary>
    public static IReadOnlyList<string> CurseForgeNameCandidates(string slug)
    {
        var name = DisplayName(slug);
        var title = name.Length == 0 ? slug : name;

        return
        [
            title,
            slug,
            char.ToUpperInvariant(slug[0]) + slug[1..],
            slug.Replace('-', ' '),
        ];
    }
}
