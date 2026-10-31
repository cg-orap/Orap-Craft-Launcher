using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Orion.Core.Game;

namespace Orion.Core.Instances;

/// <summary>一个世界（存档目录）。</summary>
public sealed record WorldEntry(
    string Directory,
    string FolderName,
    string Name,
    string? GameVersion,
    DateTimeOffset LastPlayed,
    long SizeBytes,
    long? Seed,
    string? GameType,
    bool HasDatapacks)
{
    public string SizeText => DiskUsageService.FormatBytes(SizeBytes);

    public string LastPlayedText => LastPlayed == DateTimeOffset.MinValue
        ? "从未"
        : LastPlayed.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string SeedText => Seed is null ? "" : Seed.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>一张截图。</summary>
public sealed record ScreenshotEntry(string Path, string FileName, DateTimeOffset TakenAt, long SizeBytes,
    int Width, int Height)
{
    public string SizeText => DiskUsageService.FormatBytes(SizeBytes);
    public string Dimensions => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "";
    public string TakenAtText => TakenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed record WorldBackupResult(string ZipPath, string WorldName, long SizeBytes, int FileCount)
{
    public string SizeText => DiskUsageService.FormatBytes(SizeBytes);
}

/// <summary>
/// 存档与截图管理。
///
/// 存档信息来自 <c>level.dat</c>（gzip 压缩的 NBT）。这里只解析头部几个字段，
/// 解析失败就退回"目录名 + 修改时间"，**绝不让一个坏存档把整页搞崩**。
/// 删除一律进回收站；恢复前会先把当前同名世界另存一份。
/// </summary>
public static class WorldService
{
    public static IReadOnlyList<WorldEntry> Scan(string gameDirectory, CancellationToken ct = default)
    {
        var saves = Path.Combine(gameDirectory, "saves");
        if (!Directory.Exists(saves)) return [];

        var worlds = new List<WorldEntry>();

        foreach (var directory in Directory.EnumerateDirectories(saves))
        {
            ct.ThrowIfCancellationRequested();

            var folder = Path.GetFileName(directory);
            var levelDat = Path.Combine(directory, "level.dat");
            var (bytes, _) = DiskUsageService.MeasureDirectory(directory, ct);
            var modified = Directory.Exists(directory) ? Directory.GetLastWriteTimeUtc(directory) : DateTime.UtcNow;

            var info = NbtLevelReader.TryRead(levelDat);

            worlds.Add(new WorldEntry(
                directory,
                folder,
                info?.LevelName ?? folder,
                info?.VersionName,
                info?.LastPlayed is { } played && played > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(played)
                    : new DateTimeOffset(modified, TimeSpan.Zero),
                bytes,
                info?.Seed,
                info?.GameType,
                Directory.Exists(Path.Combine(directory, "datapacks"))
                    && Directory.EnumerateFileSystemEntries(Path.Combine(directory, "datapacks")).Any()));
        }

        return [.. worlds.OrderByDescending(w => w.LastPlayed)];
    }

    /// <summary>把一个世界打包成 zip 放到备份目录，返回结果。</summary>
    public static async Task<WorldBackupResult> BackupAsync(
        string gameDirectory, string worldDirectory, string backupRoot, CancellationToken ct = default)
    {
        if (!Directory.Exists(worldDirectory))
        {
            throw new DirectoryNotFoundException($"找不到世界目录：{worldDirectory}");
        }

        Directory.CreateDirectory(backupRoot);

        var name = Path.GetFileName(worldDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(SafeName(backupRoot, $"{name}-{stamp}.zip"));

        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

            foreach (var file in Directory.EnumerateFiles(worldDirectory, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                var relative = Path.GetRelativePath(worldDirectory, file);
                var entry = zip.CreateEntry(relative.Replace('\\', '/'), CompressionLevel.Optimal);
                using var source = File.OpenRead(file);
                using var destination = entry.Open();
                source.CopyTo(destination);
            }
        }, ct).ConfigureAwait(false);

        var (bytes, files) = DiskUsageService.MeasureDirectory(worldDirectory, ct);
        return new WorldBackupResult(zipPath, name, bytes, files);
    }

    /// <summary>
    /// 从备份 zip 恢复一个世界。如果目标已存在，先把它另存一份（<c>*.before-restore</c>）。
    /// 返回恢复到的目录。
    /// </summary>
    public static async Task<string> RestoreAsync(
        string gameDirectory, string zipPath, bool overwrite = true, CancellationToken ct = default)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("找不到备份文件。", zipPath);
        }

        var saves = Path.Combine(gameDirectory, "saves");
        Directory.CreateDirectory(saves);

        var name = Path.GetFileNameWithoutExtension(zipPath);
        var index = name.LastIndexOf('-');
        // 备份文件名形如 "<世界>-yyyyMMdd-HHmmss"，去掉时间戳得到世界名
        if (index > 0)
        {
            var maybeTime = name[(index + 1)..];
            if (maybeTime.Length == 6 && maybeTime.All(char.IsDigit))
            {
                var second = name.LastIndexOf('-', index - 1);
                if (second > 0) name = name[..second];
            }
        }

        var target = Path.Combine(saves, name);

        if (Directory.Exists(target))
        {
            if (!overwrite)
            {
                throw new IOException($"世界中已存在：{name}");
            }

            var keep = target + ".before-restore-" +
                       DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            Directory.Move(target, keep);
        }

        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, target, overwriteFiles: true), ct)
            .ConfigureAwait(false);

        return target;
    }

    /// <summary>把世界移到回收站（可还原）。</summary>
    public static void Delete(string worldDirectory)
    {
        if (!Directory.Exists(worldDirectory)) return;
        RecycleBin.RemoveDirectory(worldDirectory);
    }

    private static string SafeName(string directory, string fileName)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '_');
        }

        return Path.Combine(directory, fileName);
    }
}

/// <summary>一张截图的目录扫描（只读 IHDR 头拿尺寸，不整张解码）。</summary>
public static class ScreenshotService
{
    public static IReadOnlyList<ScreenshotEntry> Scan(string gameDirectory, CancellationToken ct = default)
    {
        var directory = Path.Combine(gameDirectory, "screenshots");
        if (!Directory.Exists(directory)) return [];

        var entries = new List<ScreenshotEntry>();

        foreach (var path in Directory.EnumerateFiles(directory, "*.png"))
        {
            ct.ThrowIfCancellationRequested();

            var info = new FileInfo(path);
            var (width, height) = ReadPngSize(path);

            entries.Add(new ScreenshotEntry(
                path,
                info.Name,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                info.Length,
                width,
                height));
        }

        return [.. entries.OrderByDescending(e => e.TakenAt)];
    }

    public static void Delete(string path)
    {
        if (!File.Exists(path)) return;
        RecycleBin.RemoveFile(path);
    }

    /// <summary>只读 PNG 的 IHDR（宽高在固定偏移 16/20），失败返回 (0,0)。</summary>
    internal static (int Width, int Height) ReadPngSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) < 24) return (0, 0);

            // 8 字节签名 + 4 字节长度 + "IHDR" + 4 字节宽 + 4 字节高
            if (header[12] != 'I' || header[13] != 'H' || header[14] != 'D' || header[15] != 'R') return (0, 0);

            var width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
            var height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
            return width > 0 && height > 0 ? (width, height) : (0, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }
}

/// <summary>level.dat 里我们要的几个字段。</summary>
public sealed record LevelInfo(string? LevelName, long? Seed, string? VersionName, string? GameType,
    long LastPlayed);

/// <summary>
/// 最小 NBT 读取器：只认 TAG_String / TAG_Long / TAG_Int，其余类型整体跳过。
/// 目标字段都在根 <c>Data</c> 复合标签里；读不到就返回 null，由调用方退回目录名。
/// </summary>
public static class NbtLevelReader
{
    private const byte TagEnd = 0;
    private const byte TagByte = 1;
    private const byte TagShort = 2;
    private const byte TagInt = 3;
    private const byte TagLong = 4;
    private const byte TagFloat = 5;
    private const byte TagDouble = 6;
    private const byte TagByteArray = 7;
    private const byte TagString = 8;
    private const byte TagList = 9;
    private const byte TagCompound = 10;
    private const byte TagIntArray = 11;
    private const byte TagLongArray = 12;

    public static LevelInfo? TryRead(string levelDatPath)
    {
        if (!File.Exists(levelDatPath)) return null;

        try
        {
            using var file = File.OpenRead(levelDatPath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            gzip.CopyTo(buffer);
            buffer.Position = 0;

            using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: true);
            return ReadRoot(reader);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or EndOfStreamException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static LevelInfo? ReadRoot(BinaryReader reader)
    {
        var type = reader.ReadByte();
        if (type != TagCompound) return null;

        SkipString(reader);              // 根标签名，通常是空串

        string? levelName = null;
        long? seed = null;
        string? version = null;
        string? gameType = null;
        long lastPlayed = 0;

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var childType = reader.ReadByte();
            if (childType == TagEnd) break;

            var name = ReadString(reader);

            if (childType == TagCompound && name == "Data")
            {
                while (true)
                {
                    var dataType = reader.ReadByte();
                    if (dataType == TagEnd) break;

                    var field = ReadString(reader);
                    ParseField(reader, dataType, field,
                        ref levelName, ref seed, ref version, ref gameType, ref lastPlayed);
                }
            }
            else if (KnownFields.Contains(name))
            {
                // 少数工具生成的 level.dat 字段直接挂在根上，这里也认
                ParseField(reader, childType, name,
                    ref levelName, ref seed, ref version, ref gameType, ref lastPlayed);
            }
            else
            {
                SkipPayload(reader, childType);
            }
        }

        return new LevelInfo(levelName, seed, version, gameType, lastPlayed);
    }

    private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    {
        "LevelName", "Seed", "LastPlayed", "GameType", "Version",
    };

    private static void ParseField(BinaryReader reader, byte dataType, string field,
        ref string? levelName, ref long? seed, ref string? version, ref string? gameType, ref long lastPlayed)
    {
        switch (dataType)
        {
            case TagString when field == "LevelName":
                levelName = ReadString(reader);
                break;
            case TagLong when field == "Seed":
                seed = reader.ReadInt64();
                break;
            case TagLong when field == "LastPlayed":
                lastPlayed = reader.ReadInt64();
                break;
            case TagString when field == "GameType":
                gameType = GameTypeName(ReadString(reader));
                break;
            case TagCompound when field == "Version":
                version = ReadVersionName(reader);
                break;
            case TagInt when field == "GameType":
                gameType = GameTypeName(reader.ReadInt32());
                break;
            default:
                SkipPayload(reader, dataType);
                break;
        }
    }

    private static string? ReadVersionName(BinaryReader reader)
    {
        string? name = null;

        while (true)
        {
            var type = reader.ReadByte();
            if (type == TagEnd) break;

            var field = ReadString(reader);
            if (type == TagString && field == "Name")
            {
                name = ReadString(reader);
            }
            else
            {
                SkipPayload(reader, type);
            }
        }

        return name;
    }

    private static string GameTypeName(string raw) => raw switch
    {
        "survival" or "0" => "生存",
        "creative" or "1" => "创造",
        "adventure" or "2" => "冒险",
        "spectator" or "3" => "旁观",
        _ => raw,
    };

    private static string GameTypeName(int raw) => GameTypeName(raw.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        if (length == 0) return "";
        var bytes = reader.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void SkipString(BinaryReader reader) => _ = ReadString(reader);

    private static void SkipPayload(BinaryReader reader, byte type)
    {
        switch (type)
        {
            case TagByte:
                reader.ReadByte();
                break;
            case TagShort:
                reader.ReadInt16();
                break;
            case TagInt:
            case TagFloat:
                reader.ReadInt32();
                break;
            case TagLong:
            case TagDouble:
                reader.ReadInt64();
                break;
            case TagByteArray:
                SkipFixed(reader, reader.ReadInt32());
                break;
            case TagString:
                SkipString(reader);
                break;
            case TagList:
                var itemType = reader.ReadByte();
                var count = reader.ReadInt32();
                for (var i = 0; i < count; i++) SkipPayload(reader, itemType);
                break;
            case TagCompound:
                while (true)
                {
                    var type2 = reader.ReadByte();
                    if (type2 == TagEnd) break;
                    SkipString(reader);
                    SkipPayload(reader, type2);
                }

                break;
            case TagIntArray:
                SkipFixed(reader, reader.ReadInt32() * 4);
                break;
            case TagLongArray:
                SkipFixed(reader, reader.ReadInt32() * 8);
                break;
            default:
                throw new InvalidDataException($"未知的 NBT 标签类型：{type}");
        }
    }

    private static void SkipFixed(BinaryReader reader, int bytes)
    {
        if (bytes <= 0) return;
        reader.BaseStream.Seek(bytes, SeekOrigin.Current);
    }
}
