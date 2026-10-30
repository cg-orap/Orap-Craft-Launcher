using System.IO.Compression;

namespace Orion.Core.Mods;

/// <summary>整合包里的一个文件该放到实例的哪个目录。</summary>
public enum PackFileKind
{
    /// <summary>模组（有 mods.toml / fabric.mod.json）。</summary>
    Mod,

    /// <summary>资源包（有 pack.mcmeta + assets/）。</summary>
    ResourcePack,

    /// <summary>光影包（有 shaders/ 或 shaderFile_*）。</summary>
    ShaderPack,

    /// <summary>认不出来的，按模组处理（保持旧行为，别丢用户文件）。</summary>
    Unknown,
}

/// <summary>
/// 按内容判断整合包里的文件该放哪。
///
/// CurseForge 的 manifest 只给 projectID/fileID，不告诉你是模组还是资源包；以前一律扔进 mods/，
/// 结果 NeoForge 会因为"File ... is not a valid mod file"直接判定 mod loading 失败。
/// </summary>
public static class PackFileClassifier
{
    private static readonly string[] ShaderHints = ["shaders/", "shaderFile", "shaderpacks/"];

    /// <summary>看 zip 内容判断类型；打不开的文件一律当 Unknown。</summary>
    public static PackFileKind Classify(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
            if (names.Count == 0) return PackFileKind.Unknown;

            // 1) 明确的模组标记：mods.toml / neoforge.mods.toml / fabric.mod.json
            if (names.Any(n => n.Equals("META-INF/mods.toml", StringComparison.OrdinalIgnoreCase)
                               || n.Equals("META-INF/neoforge.mods.toml", StringComparison.OrdinalIgnoreCase)
                               || n.Equals("fabric.mod.json", StringComparison.OrdinalIgnoreCase)))
            {
                return PackFileKind.Mod;
            }

            // 2) 光影：shaders 目录（含 common 布局）或旧式 shaderFile_*
            if (names.Any(n => n.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase)
                               || n.StartsWith("shaderFile", StringComparison.OrdinalIgnoreCase)
                               || n.StartsWith("shaderpacks/", StringComparison.OrdinalIgnoreCase)
                               || n.Contains("/shaders/", StringComparison.OrdinalIgnoreCase)))
            {
                return PackFileKind.ShaderPack;
            }

            // 3) 资源包：pack.mcmeta + assets/
            var hasPackMeta = names.Any(n => n.Equals("pack.mcmeta", StringComparison.OrdinalIgnoreCase));
            var hasAssets = names.Any(n => n.StartsWith("assets/", StringComparison.OrdinalIgnoreCase));
            if (hasPackMeta && hasAssets) return PackFileKind.ResourcePack;

            return PackFileKind.Unknown;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return PackFileKind.Unknown;
        }
    }

    /// <summary>类型对应的目录名；Unknown 留在 mods。</summary>
    public static string FolderFor(PackFileKind kind) => kind switch
    {
        PackFileKind.ResourcePack => "resourcepacks",
        PackFileKind.ShaderPack => "shaderpacks",
        _ => "mods",
    };

    /// <summary>
    /// 把一个已经落地的文件搬到正确的目录；返回最终路径（已经在正确目录时原样返回）。
    /// 资源包/光影统一改成 .zip 后缀，否则游戏不认。
    /// </summary>
    public static string Route(string path, string gameDirectory, out PackFileKind kind)
    {
        kind = Classify(path);
        if (kind is PackFileKind.Mod or PackFileKind.Unknown) return path;

        var targetDirectory = Path.Combine(gameDirectory, FolderFor(kind));
        Directory.CreateDirectory(targetDirectory);

        var fileName = Path.GetFileName(path);
        if (kind != PackFileKind.Mod && fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            fileName = Path.ChangeExtension(fileName, ".zip");
        }

        var target = Path.Combine(targetDirectory, fileName);
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (File.Exists(target)) File.Delete(target);
        File.Move(path, target);
        return target;
    }
}
