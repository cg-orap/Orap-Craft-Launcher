using System.Text;
using System.Text.RegularExpressions;
using Orion.Core.Diagnostics;

namespace Orion.Core.Launch;

public sealed record CrashAnalysis(
    string Summary,
    IReadOnlyList<string> Causes,
    IReadOnlyList<string> Evidence,
    string? CrashReportPath)
{
    public bool IsCrash => Causes.Count > 0;
}

/// <summary>
/// Turns a non-zero exit code plus the tail of the game log into an explanation a user can
/// act on. This is diagnostic heuristics, not a guarantee: every finding is presented with
/// the evidence line that produced it.
/// </summary>
public static class CrashAnalyzer
{
    private sealed record Rule(Regex Pattern, string Cause);

    private static readonly Rule[] Rules =
    [
        new(new Regex(@"java\.lang\.OutOfMemoryError", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "内存不足：请在实例设置中提高最大内存，或减少 Mod / 资源包数量。"),
        new(new Regex(@"UnsupportedClassVersionError.*?class file version (\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Java 版本过低：该游戏版本或 Mod 需要更高版本的 Java。"),
        new(new Regex(@"has been compiled by a more recent version", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Java 版本过低：请更换更高版本的 Java 运行时。"),
        new(new Regex(@"Missing or unsupported mandatory dependencies", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Mod 依赖缺失：请检查 Mod 列表中缺失的前置 Mod。"),
        new(new Regex(@"java\.lang\.NoClassDefFoundError: (net/minecraft|net/minecraftforge|net/neoforged)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "游戏文件不完整：客户端 JAR 或依赖库缺失，请重新安装该版本或执行完整性修复。"),
        new(new Regex(@"Could not find or load main class", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "主类缺失：客户端 JAR 可能损坏，请重新下载。"),
        new(new Regex(@"Mixin apply(ing)? .*failed|MixinTransformerError|InvalidMixinException", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Mixin 注入失败：通常由 Mod 与当前加载器版本不兼容引起。"),
        new(new Regex(@"Pixel format not accelerated|Failed to create (GLFW )?window|GLFW error|WGL: Failed", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "图形初始化失败：请更新显卡驱动，或检查显示缩放 / 远程桌面环境。"),
        new(new Regex(@"Duplicate mods|DuplicateModsFoundException", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "存在重复的 Mod：同一 Mod 的多个版本被同时加载。"),
        new(new Regex(@"java\.lang\.IllegalAccessError|NoSuchMethodError|NoSuchFieldError", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "类 / 方法不匹配：通常是 Mod 与游戏或加载器版本不兼容，或依赖库版本冲突。"),
        new(new Regex(@"The game crashed whilst", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "游戏自身报告了崩溃，详见 crash-reports 目录。"),
        new(new Regex(@"Address already in use|Failed to bind to port", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "端口被占用：可能是另一个游戏实例仍在运行。"),
        new(new Regex(@"Authentication (is )?required|Invalid session|Failed to verify username", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "会话验证失败：在线模式服务器需要正版账户登录，离线账户无法加入。"),
        new(new Regex(@"java\.io\.FileNotFoundException.*(options\.txt|saves)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "文件访问失败：请检查游戏目录是否可写，或存档是否被其他程序占用。"),
    ];

    public static async Task<CrashAnalysis> AnalyzeAsync(
        string gameDirectory,
        int exitCode,
        IReadOnlyList<string> recentLogLines,
        CancellationToken ct = default)
    {
        var causes = new List<string>();
        var evidence = new List<string>();
        var crashReportPath = FindNewestCrashReport(gameDirectory);

        // 1. Known log signatures.
        foreach (var rule in Rules)
        {
            foreach (var line in recentLogLines)
            {
                if (!rule.Pattern.IsMatch(line)) continue;

                if (!causes.Contains(rule.Cause)) causes.Add(rule.Cause);
                if (evidence.Count < 12) evidence.Add(Trim(line));
                break;
            }
        }

        // 2. The crash report itself is the most reliable source.
        if (crashReportPath is not null)
        {
            var details = await ReadCrashReportAsync(crashReportPath, ct).ConfigureAwait(false);
            if (details is not null)
            {
                if (!string.IsNullOrWhiteSpace(details.Value.Description))
                    evidence.Insert(0, "Crash report – " + details.Value.Description);

                if (!string.IsNullOrWhiteSpace(details.Value.ExceptionType) && !causes.Any(c => c.Contains("Mixin")))
                    causes.Add($"游戏抛出异常 {details.Value.ExceptionType}，详见崩溃报告。");
            }
        }

        // 3. Windows specific exit codes.
        causes.AddRange(InterpretExitCode(exitCode));

        if (causes.Count == 0 && exitCode != 0)
        {
            causes.Add($"游戏异常退出 (退出代码 {exitCode})，未匹配到已知原因，请查看日志末尾。");
        }

        var summary = causes.Count > 0
            ? causes[0]
            : exitCode == 0 ? "游戏已正常退出。" : "游戏意外结束。";

        return new CrashAnalysis(summary, causes, evidence, crashReportPath);
    }

    private static IEnumerable<string> InterpretExitCode(int exitCode)
    {
        // Windows NTSTATUS values are surfaced as signed 32-bit exit codes by .NET.
        switch ((uint)exitCode)
        {
            case 0xC0000005:
                yield return "游戏以访问冲突 (0xC0000005) 结束：通常是显卡驱动或原生库 (Mod) 崩溃。";
                break;
            case 0xC000001D:
                yield return "非法指令 (0xC000001D)：CPU 指令集或原生库与当前平台不匹配。";
                break;
            case 0xC0000135:
                yield return "缺少 DLL (0xC0000135)：请检查是否缺少 Visual C++ 运行库或原生库。";
                break;
            case 0xC0000409:
                yield return "栈缓冲区溢出 (0xC0000409)：原生库 (Mod) 主动终止了进程。";
                break;
        }
    }

    private static async Task<(string? Description, string? ExceptionType)?> ReadCrashReportAsync(string path, CancellationToken ct)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
            string? description = null;
            string? exceptionType = null;

            foreach (var line in lines.Take(40))
            {
                if (description is null && line.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
                    description = line["Description:".Length..].Trim();

                if (exceptionType is null)
                {
                    var match = Regex.Match(line, @"^(?<type>[a-z][\w$]*(\.[\w$]+)+(:|$))", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                    if (match.Success) exceptionType = match.Groups["type"].Value.TrimEnd(':');
                }
            }

            return (description, exceptionType);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? FindNewestCrashReport(string gameDirectory)
    {
        var directory = Path.Combine(gameDirectory, "crash-reports");
        if (!Directory.Exists(directory)) return null;

        try
        {
            return Directory.EnumerateFiles(directory, "crash-*.txt")
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Trim(string line) => line.Length <= 240 ? line : line[..240] + "…";
}
