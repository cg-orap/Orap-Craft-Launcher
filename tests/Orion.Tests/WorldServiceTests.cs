using System.IO.Compression;
using System.Text;
using Orion.Core.Instances;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 存档 / 截图管理。
/// 重点：坏 level.dat 不能让页面崩、备份恢复往返一致、删除进回收站前不误伤别的世界。
/// </summary>
public sealed class WorldServiceTests
{
    [Fact]
    public void ScanningReadsLevelNameSeedAndVersion()
    {
        using var game = new GameDir();
        game.World("My World", levelName: "我的世界", seed: 123456789, version: "1.21.1", gameType: "creative",
            lastPlayed: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var world = Assert.Single(WorldService.Scan(game.Path));

        Assert.Equal("我的世界", world.Name);
        Assert.Equal("1.21.1", world.GameVersion);
        Assert.Equal(123456789, world.Seed);
        Assert.Equal("创造", world.GameType);
    }

    [Fact]
    public void CorruptLevelDatFallsBackToFolderName()
    {
        using var game = new GameDir();
        var world = game.World("Broken World", levelName: "irrelevant", seed: 1, version: "1.21.1", gameType: "survival",
            lastPlayed: 0);
        File.WriteAllBytes(Path.Combine(world, "level.dat"), [0x1F, 0x8B, 0x08, 0x00, 0x00]);   // 截断的 gzip

        var entry = Assert.Single(WorldService.Scan(game.Path));

        Assert.Equal("Broken World", entry.Name);      // 退回目录名
        Assert.Null(entry.GameVersion);
        Assert.Null(entry.Seed);
    }

    [Fact]
    public void MissingLevelDatStillListsTheWorld()
    {
        using var game = new GameDir();
        var world = game.World("NoLevel", levelName: "x", seed: 0, version: null, gameType: null, lastPlayed: 0);
        File.Delete(Path.Combine(world, "level.dat"));

        var entry = Assert.Single(WorldService.Scan(game.Path));

        Assert.Equal("NoLevel", entry.Name);
        Assert.True(entry.LastPlayed > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task BackupThenRestoreRoundTripsTheWorld()
    {
        using var game = new GameDir();
        var world = game.World("Roundtrip", levelName: "RT", seed: 42, version: "1.21.1", gameType: "survival",
            lastPlayed: 0);
        File.WriteAllText(Path.Combine(world, "level.dat.txt"), "original");

        var backupRoot = Path.Combine(game.Path, "..", "world-backups-" + Guid.NewGuid().ToString("N"));
        var result = await WorldService.BackupAsync(game.Path, world, backupRoot);

        Assert.True(File.Exists(result.ZipPath));
        Assert.Equal("Roundtrip", result.WorldName);

        // 改坏文件，再恢复
        File.WriteAllText(Path.Combine(world, "level.dat.txt"), "modified");
        var restored = await WorldService.RestoreAsync(game.Path, result.ZipPath);

        Assert.Equal("Roundtrip", Path.GetFileName(restored));
        Assert.Equal("original", File.ReadAllText(Path.Combine(restored, "level.dat.txt")));

        // 恢复前把旧目录另存了一份
        Assert.NotEmpty(Directory.GetDirectories(game.Path, "Roundtrip.before-restore-*",
            SearchOption.TopDirectoryOnly).Length > 0
            ? Directory.GetDirectories(game.Path, "Roundtrip.before-restore-*")
            : Directory.GetDirectories(Path.Combine(game.Path, "saves"), "Roundtrip.before-restore-*"));

        Directory.Delete(backupRoot, recursive: true);
    }

    [Fact]
    public void ScreenshotScanReadsPngDimensionsFromTheHeader()
    {
        using var game = new GameDir();
        game.Png("2026-03-01_12.00.00.png", 64, 36);

        var shot = Assert.Single(ScreenshotService.Scan(game.Path));

        Assert.Equal(64, shot.Width);
        Assert.Equal(36, shot.Height);
        Assert.Equal("64×36", shot.Dimensions);
    }

    [Fact]
    public void BrokenPngIsStillListedWithoutDimensions()
    {
        using var game = new GameDir();
        var path = Path.Combine(game.Path, "screenshots", "broken.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);

        var shot = Assert.Single(ScreenshotService.Scan(game.Path));

        Assert.Equal(0, shot.Width);
        Assert.Equal("", shot.Dimensions);
    }

    [Fact]
    public void MissingFoldersReturnEmptyLists()
    {
        using var game = new GameDir();

        Assert.Empty(WorldService.Scan(game.Path));
        Assert.Empty(ScreenshotService.Scan(game.Path));
    }

    [Fact]
    public void WorldsAreSortedByLastPlayed()
    {
        using var game = new GameDir();
        game.World("Old", "Old", 1, "1.20.1", "survival", DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds());
        game.World("New", "New", 2, "1.21.1", "creative", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var worlds = WorldService.Scan(game.Path);

        Assert.Equal("New", worlds[0].Name);
        Assert.Equal("Old", worlds[1].Name);
    }

    private sealed class GameDir : IDisposable
    {
        public GameDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orap-world-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string World(string folder, string levelName, long seed, string? version, string? gameType, long lastPlayed)
        {
            var directory = System.IO.Path.Combine(Path, "saves", folder);
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(System.IO.Path.Combine(directory, "region"));

            File.WriteAllText(System.IO.Path.Combine(directory, "level.dat"), "");
            WriteLevelDat(System.IO.Path.Combine(directory, "level.dat"), levelName, seed, version, gameType, lastPlayed);
            File.WriteAllBytes(System.IO.Path.Combine(directory, "region", "r.0.0.mca"), new byte[2048]);
            return directory;
        }

        public void Png(string fileName, int width, int height)
        {
            var directory = System.IO.Path.Combine(Path, "screenshots");
            Directory.CreateDirectory(directory);

            var bytes = new byte[24];
            // PNG 签名
            bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
            bytes[4] = 0x0D; bytes[5] = 0x0A; bytes[6] = 0x1A; bytes[7] = 0x0A;
            // 长度 13 + "IHDR"
            bytes[8] = 0; bytes[9] = 0; bytes[10] = 0; bytes[11] = 13;
            bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
            // 宽高（大端）
            bytes[16] = (byte)(width >> 24); bytes[17] = (byte)(width >> 16); bytes[18] = (byte)(width >> 8); bytes[19] = (byte)width;
            bytes[20] = (byte)(height >> 24); bytes[21] = (byte)(height >> 16); bytes[22] = (byte)(height >> 8); bytes[23] = (byte)height;

            File.WriteAllBytes(System.IO.Path.Combine(directory, fileName), bytes);
        }

        /// <summary>手工拼一个最小 level.dat：gzip(TAG_Compound{ Data: {...} })。</summary>
        private static void WriteLevelDat(string path, string levelName, long seed, string? version, string? gameType, long lastPlayed)
        {
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((byte)10);            // TAG_Compound 根
                WriteString(writer, "");           // 根名

                WriteStringTag(writer, "LevelName", levelName);
                WriteLongTag(writer, "Seed", seed);
                WriteLongTag(writer, "LastPlayed", lastPlayed);

                if (version is not null)
                {
                    writer.Write((byte)10);        // Data.Version 复合标签
                    WriteString(writer, "Version");
                    WriteStringTag(writer, "Name", version);
                    writer.Write((byte)0);         // 结束
                }

                if (gameType is not null)
                {
                    WriteStringTag(writer, "GameType", gameType);
                }

                writer.Write((byte)0);             // Data 结束
                writer.Write((byte)0);             // 根结束
            }

            using var file = File.Create(path);
            using var gzip = new GZipStream(file, CompressionLevel.Optimal);
            buffer.Position = 0;
            buffer.CopyTo(gzip);
        }

        private static void WriteStringTag(BinaryWriter writer, string name, string value)
        {
            writer.Write((byte)8);
            WriteString(writer, name);
            WriteString(writer, value);
        }

        private static void WriteLongTag(BinaryWriter writer, string name, long value)
        {
            writer.Write((byte)4);
            WriteString(writer, name);
            writer.Write(value);
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
