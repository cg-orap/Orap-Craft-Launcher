using System.Globalization;
using System.Text;
using Orion.Core.Diagnostics;

namespace Orion.Core.Game;

/// <summary>
/// 新版本初始化用的游戏内设置模板（写进该版本自己的 <c>options.txt</c>）。
///
/// 目的：每装一个新版本都要重新调一遍视距、界面尺寸、自动跳跃这些选项，很浪费时间。这里把
/// 常调的几项做成默认值，新版本建好后立即写入，老版本也可以随时"应用一次"。
///
/// 只合并这几项，其余键（按键绑定、资源包、视角等）原样保留，绝不整文件覆盖。
/// </summary>
public sealed class GameOptionsTemplate
{
    public bool Enabled { get; set; } = true;
    public int RenderDistance { get; set; } = 12;
    public int GuiScale { get; set; } = 0;
    public bool AutoJump { get; set; }
    public int Fov { get; set; } = 70;
    public double Gamma { get; set; } = 0.5;
    public string Particles { get; set; } = "all";
    public bool BobView { get; set; } = true;

    /// <summary>启动器启动时注入的当前模板；InstanceManager 建新版本时用它。</summary>
    public static GameOptionsTemplate Default { get; set; } = new();

    public const string FileName = "options.txt";

    /// <summary>写入 options.txt 的键值（键名与游戏内一致）。</summary>
    public IReadOnlyDictionary<string, string> ToKeyValues() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["renderDistance"] = Math.Clamp(RenderDistance, 2, 32).ToString(CultureInfo.InvariantCulture),
        ["guiScale"] = Math.Clamp(GuiScale, 0, 4).ToString(CultureInfo.InvariantCulture),
        ["autoJump"] = AutoJump ? "true" : "false",
        ["fov"] = Math.Clamp(Fov, 30, 110).ToString(CultureInfo.InvariantCulture),
        ["gamma"] = Math.Clamp(Gamma, 0, 1).ToString("0.0", CultureInfo.InvariantCulture),
        ["particles"] = NormaliseParticles(Particles),
        ["bobView"] = BobView ? "true" : "false",
    };

    public string Summary =>
        $"视距 {RenderDistance} · 界面尺寸 {(GuiScale == 0 ? "自动" : GuiScale.ToString(CultureInfo.InvariantCulture))} · "
        + $"自动跳跃 {(AutoJump ? "开" : "关")} · FOV {Fov}";

    private static string NormaliseParticles(string value) => value switch
    {
        "minimal" or "decreased" or "all" => value,
        _ => "all",
    };

    /// <summary>
    /// 把模板合并进 <paramref name="existing"/>（options.txt 原文）。
    /// 已有键就地改值，缺失键追加到末尾，注释与其他键原样保留。
    /// </summary>
    public static string Merge(string? existing, IReadOnlyDictionary<string, string> values)
    {
        var lines = (existing ?? "").Replace("\r\n", "\n").Split('\n').ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var key = line[..colon].Trim();
            if (!values.TryGetValue(key, out var value)) continue;

            lines[i] = key + ":" + value;
            seen.Add(key);
        }

        foreach (var (key, value) in values)
        {
            if (seen.Contains(key)) continue;
            lines.Add(key + ":" + value);
        }

        // 去掉合并产生的空尾行，保持文件以单个换行结尾。
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>把模板写进某个版本的 options.txt；返回写入的键数。</summary>
    public int Apply(string gameDirectory)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(gameDirectory)) return 0;

        Directory.CreateDirectory(gameDirectory);
        var path = Path.Combine(gameDirectory, FileName);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        var values = ToKeyValues();

        File.WriteAllText(path, Merge(existing, values), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        AppLog.Info("toolbox", "game-options-applied",
            ("dir", gameDirectory),
            ("keys", values.Count),
            ("created", existing is null));
        return values.Count;
    }

    /// <summary>新版本建好后调用：使用当前默认模板；模板关闭时什么都不做。</summary>
    public static int ApplyDefaults(string gameDirectory) => Default.Apply(gameDirectory);
}
