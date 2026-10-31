using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Orion.Core.Mods;

/// <summary>一个资源包 / 光影包 / 数据包的条目。</summary>
public sealed record ContentPackEntry(
    string FilePath,
    string FileName,
    string DisplayName,
    string? Description,
    string? Format,
    long SizeBytes,
    bool Enabled,
    int Priority,
    bool IsDirectory)
{
    public string SizeText => SizeBytes switch
    {
        > 1024L * 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024 / 1024:0.0} GB",
        > 1024L * 1024 => $"{SizeBytes / 1024.0 / 1024:0.0} MB",
        > 1024 => $"{SizeBytes / 1024.0:0} KB",
        _ => $"{SizeBytes} B",
    };
}

/// <summary>数据包：挂在某个世界下面。</summary>
public sealed record DatapackEntry(
    string FilePath,
    string FileName,
    string WorldName,
    long SizeBytes,
    bool IsDirectory);

/// <summary>
/// 资源包 / 光影包 / 数据包的本地管理。
///
/// 三类的"启用"机制完全不同：
/// * 资源包——<c>options.txt</c> 里的 <c>resourcePacks</c> 数组，**顺序即优先级**；
/// * 光影包——Iris/Oculus 的 <c>config/iris.properties</c> 里的 <c>shaderPack=</c>；
/// * 数据包——要改世界 <c>level.dat</c> 的 NBT，风险太高，这里**只读**（列出/删除/打开）。
///
/// 写文件的原则：只替换目标那一行，其余字节原样保留（换行符、注释、未知键都不动），
/// 写前先备份一次 <c>.orap.bak</c>，因为 options.txt 一旦写坏游戏会把设置全重置。
/// </summary>
public static class ContentInventoryService
{
    public const string ResourcePacksDir = "resourcepacks";
    public const string ShaderPacksDir = "shaderpacks";
    public const string SavesDir = "saves";

    // ── 资源包 ────────────────────────────────────────────────────────────────
    public static IReadOnlyList<ContentPackEntry> ScanResourcePacks(string gameDirectory)
    {
        var directory = Path.Combine(gameDirectory, ResourcePacksDir);
        if (!Directory.Exists(directory)) return [];

        var enabled = ReadResourcePackOrder(gameDirectory);
        var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < enabled.Count; i++) orderIndex[enabled[i]] = i;

        var entries = new List<ContentPackEntry>();

        foreach (var path in EnumerateContentFiles(directory))
        {
            var fileName = Path.GetFileName(path);
            var isDirectory = Directory.Exists(path);
            var (name, description, format) = ReadPackMeta(path, isDirectory);
            var inside = orderIndex.TryGetValue(fileName, out var priority);

            entries.Add(new ContentPackEntry(
                path,
                fileName,
                name ?? Path.GetFileNameWithoutExtension(fileName),
                description,
                format,
                isDirectory ? 0 : new FileInfo(path).Length,
                inside,
                inside ? priority : -1,
                isDirectory));
        }

        return [.. entries
            .OrderByDescending(e => e.Enabled)
            .ThenBy(e => e.Priority)
            .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// 按给定顺序启用资源包（数组顺序 = 优先级，靠前的优先）。
    /// 只改 options.txt 的 resourcePacks 一行，其它内容一个字节都不动。
    /// </summary>
    public static async Task SetResourcePacksAsync(
        string gameDirectory, IReadOnlyList<string> orderedFileNames, CancellationToken ct = default)
    {
        var optionsPath = Path.Combine(gameDirectory, "options.txt");
        var text = File.Exists(optionsPath) ? await File.ReadAllTextAsync(optionsPath, ct).ConfigureAwait(false) : "";

        var payload = new List<string> { "vanilla" };
        payload.AddRange(orderedFileNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => "file/" + name));

        var value = JsonSerializer.Serialize(payload);
        var updated = OptionText.ReplaceValue(text, "resourcePacks", value);

        await BackupAndWriteAsync(optionsPath, updated, ct).ConfigureAwait(false);
    }

    // ── 光影包 ────────────────────────────────────────────────────────────────
    public static IReadOnlyList<ContentPackEntry> ScanShaderPacks(string gameDirectory)
    {
        var directory = Path.Combine(gameDirectory, ShaderPacksDir);
        if (!Directory.Exists(directory)) return [];

        var configured = ReadShaderPackName(gameDirectory);

        var entries = new List<ContentPackEntry>();
        foreach (var path in EnumerateContentFiles(directory))
        {
            var fileName = Path.GetFileName(path);
            var isDirectory = Directory.Exists(path);

            entries.Add(new ContentPackEntry(
                path,
                fileName,
                Path.GetFileNameWithoutExtension(fileName),
                null,
                null,
                isDirectory ? 0 : new FileInfo(path).Length,
                !string.IsNullOrWhiteSpace(configured)
                    && (configured.Equals(fileName, StringComparison.OrdinalIgnoreCase)
                        || configured.Equals(Path.GetFileNameWithoutExtension(fileName), StringComparison.OrdinalIgnoreCase)),
                0,
                isDirectory));
        }

        return [.. entries
            .OrderByDescending(e => e.Enabled)
            .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>选择光影包；传 null 表示关闭光影（写 shaderPack = (off)）。</summary>
    public static async Task SetShaderPackAsync(
        string gameDirectory, string? fileName, CancellationToken ct = default)
    {
        var propertiesPath = ShaderPropertiesPath(gameDirectory);
        if (propertiesPath is null) return;      // 没装 Iris/Oculus，不猜路径

        var text = File.Exists(propertiesPath) ? await File.ReadAllTextAsync(propertiesPath, ct).ConfigureAwait(false) : "";
        var value = string.IsNullOrWhiteSpace(fileName) ? "(off)" : fileName;
        var updated = OptionText.ReplaceValue(text, "shaderPack", value, separator: '=');

        await BackupAndWriteAsync(propertiesPath, updated, ct).ConfigureAwait(false);
    }

    /// <summary>Iris / Oculus 的配置文件路径（存在哪个用哪个）。</summary>
    public static string? ShaderPropertiesPath(string gameDirectory)
    {
        var config = Path.Combine(gameDirectory, "config");
        foreach (var candidate in new[] { "iris.properties", "oculus.properties" })
        {
            var path = Path.Combine(config, candidate);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    // ── 数据包（只读） ────────────────────────────────────────────────────────
    public static IReadOnlyList<DatapackEntry> ScanDatapacks(string gameDirectory)
    {
        var saves = Path.Combine(gameDirectory, SavesDir);
        if (!Directory.Exists(saves)) return [];

        var entries = new List<DatapackEntry>();

        foreach (var world in Directory.EnumerateDirectories(saves))
        {
            var datapacks = Path.Combine(world, "datapacks");
            if (!Directory.Exists(datapacks)) continue;

            foreach (var path in EnumerateContentFiles(datapacks))
            {
                var isDirectory = Directory.Exists(path);
                entries.Add(new DatapackEntry(
                    path,
                    Path.GetFileName(path),
                    Path.GetFileName(world),
                    isDirectory ? 0 : new FileInfo(path).Length,
                    isDirectory));
            }
        }

        return [.. entries
            .OrderBy(e => e.WorldName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.FileName, StringComparer.CurrentCultureIgnoreCase)];
    }

    // ── 内部工具 ──────────────────────────────────────────────────────────────
    private static IEnumerable<string> EnumerateContentFiles(string directory)
    {
        foreach (var zip in Directory.EnumerateFiles(directory, "*.zip")) yield return zip;

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            // 目录形式的内容包：里面必须有 pack.mcmeta，否则多半是用户放的杂物
            if (File.Exists(Path.Combine(child, "pack.mcmeta"))) yield return child;
        }
    }

    /// <summary>资源包顺序：options.txt 里 resourcePacks 数组，去掉 vanilla，去掉 file/ 前缀。</summary>
    internal static List<string> ReadResourcePackOrder(string gameDirectory)
    {
        var optionsPath = Path.Combine(gameDirectory, "options.txt");
        if (!File.Exists(optionsPath)) return [];

        var raw = OptionText.ReadValue(File.ReadAllText(optionsPath), "resourcePacks");
        if (string.IsNullOrWhiteSpace(raw)) return [];

        try
        {
            var list = JsonSerializer.Deserialize<string[]>(raw);
            if (list is null) return [];

            return [.. list
                .Where(item => !item.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.StartsWith("file/", StringComparison.OrdinalIgnoreCase) ? item[5..] : item)
                .Where(item => item.Length > 0)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static string? ReadShaderPackName(string gameDirectory)
    {
        var path = ShaderPropertiesPath(gameDirectory);
        if (path is null) return null;

        var value = OptionText.ReadValue(File.ReadAllText(path), "shaderPack", separator: '=');
        if (string.IsNullOrWhiteSpace(value) || value.Contains("(off)", StringComparison.OrdinalIgnoreCase)) return null;
        return value;
    }

    private static (string? Name, string? Description, string? Format) ReadPackMeta(string path, bool isDirectory)
    {
        try
        {
            string json;
            if (isDirectory)
            {
                json = File.ReadAllText(Path.Combine(path, "pack.mcmeta"));
            }
            else
            {
                using var zip = ZipFile.OpenRead(path);
                var entry = zip.GetEntry("pack.mcmeta");
                if (entry is null) return (null, null, null);
                using var reader = new StreamReader(entry.Open());
                json = reader.ReadToEnd();
            }

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("pack", out var pack)) return (null, null, null);

            var name = pack.TryGetProperty("name", out var n) ? n.GetString() : null;
            var description = pack.TryGetProperty("description", out var d) ? Describe(d) : null;
            var format = pack.TryGetProperty("pack_format", out var f) && f.TryGetInt32(out var value)
                ? value.ToString(CultureInfo.InvariantCulture)
                : null;

            return (Clean(name), Clean(description), format);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return (null, null, null);
        }
    }

    /// <summary>description 可能是字符串，也可能是 {"text": "..."} 或数组。</summary>
    private static string? Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Object when element.TryGetProperty("text", out var text) => text.GetString(),
        JsonValueKind.Array => string.Join("", element.EnumerateArray().Select(Describe)),
        _ => null,
    };

    /// <summary>去掉 § 颜色代码和多余空白。</summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '§' && i + 1 < value.Length)
            {
                i++;
                continue;
            }

            builder.Append(value[i]);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static async Task BackupAndWriteAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            var backup = path + ".orap.bak";
            File.Copy(path, backup, overwrite: true);
        }

        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Minecraft 配置文件的"只改一行"工具：options.txt 用的是 <c>key:value</c>，
/// properties 用的是 <c>key=value</c>。其余字节（注释、未知键、换行符）一律原样保留。
/// </summary>
public static class OptionText
{
    public static string? ReadValue(string text, string key, char separator = ':')
    {
        foreach (var (line, _) in EnumerateLines(text))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(key + separator.ToString(), StringComparison.Ordinal)) continue;
            return trimmed[(key.Length + 1)..].Trim();
        }

        return null;
    }

    /// <summary>
    /// 替换 <paramref name="key"/> 的值；不存在就追加一行。
    /// 返回的字符串与原串相比只有那一行不同。
    /// </summary>
    public static string ReplaceValue(string text, string key, string value, char separator = ':')
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var replacement = key + separator + value;

        var lines = SplitKeepingEmpty(text);
        var found = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith(key + separator.ToString(), StringComparison.Ordinal)) continue;

            lines[i] = replacement;
            found = true;
            break;
        }

        if (!found)
        {
            if (lines.Count > 0 && lines[^1].Length == 0) lines.Insert(lines.Count - 1, replacement);
            else lines.Add(replacement);
        }

        return string.Join(newline, lines);
    }

    private static IEnumerable<(string Line, int Index)> EnumerateLines(string text)
    {
        if (text.Length == 0) yield break;

        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] != '\n') continue;

            var end = i;
            if (end > start && text[end - 1] == '\r') end--;
            yield return (text[start..end], start);

            start = i + 1;
        }
    }

    /// <summary>按 \n 切分但保留行内容（不丢空行），用于逐行改写。</summary>
    private static List<string> SplitKeepingEmpty(string text)
    {
        var lines = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var end = i;
            if (end > start && text[end - 1] == '\r') end--;
            lines.Add(text[start..end]);
            start = i + 1;
        }

        lines.Add(text[start..]);
        return lines;
    }
}
