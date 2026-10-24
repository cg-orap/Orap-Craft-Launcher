using System.IO.Compression;

namespace Orion.Core.Game;

/// <summary>
/// Extracts native libraries (<c>.dll</c> on Windows) out of LWJGL native jars.
///
/// Rules follow the official launcher: entries listed in the library's
/// <c>extract.exclude</c> array are skipped (default <c>META-INF/</c>), and extraction is
/// idempotent so a partially extracted directory is repaired rather than trusted.
/// </summary>
public static class NativeExtractor
{
    private static readonly string[] DefaultExcludes = ["META-INF/"];

    public sealed record ExtractResult(int FilesWritten, int FilesSkipped, IReadOnlyList<string> Errors);

    public static async Task<ExtractResult> ExtractAsync(
        IEnumerable<LibraryFile> nativeJars,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDirectory);

        var written = 0;
        var skipped = 0;
        var errors = new List<string>();
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var jar in nativeJars)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(jar.AbsolutePath))
            {
                errors.Add($"缺失本地库文件: {jar.AbsolutePath}");
                continue;
            }

            try
            {
                using var archive = ZipFile.OpenRead(jar.AbsolutePath);
                var excludes = jar.ExtractExclude is { Count: > 0 }
                    ? jar.ExtractExclude.Concat(DefaultExcludes).ToArray()
                    : DefaultExcludes;

                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

                    var normalized = entry.FullName.Replace('\\', '/');
                    if (excludes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Refuse path traversal (zip-slip) unconditionally.
                    var destination = Path.GetFullPath(Path.Combine(destinationDirectory, normalized));
                    if (!destination.StartsWith(Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"跳过可疑路径: {entry.FullName}");
                        continue;
                    }

                    if (!extracted.Add(destination))
                    {
                        skipped++;
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                    if (File.Exists(destination) && new FileInfo(destination).Length == entry.Length)
                    {
                        skipped++;
                        continue;
                    }

                    await using var source = entry.Open();
                    await using var target = File.Create(destination);
                    await source.CopyToAsync(target, ct).ConfigureAwait(false);
                    written++;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(jar.AbsolutePath)}: {ex.Message}");
            }
        }

        return new ExtractResult(written, skipped, errors);
    }

    /// <summary>True when the directory already contains at least one native library.</summary>
    public static bool HasNatives(string directory)
    {
        if (!Directory.Exists(directory)) return false;

        try
        {
            return Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Any()
                   || Directory.EnumerateFiles(directory, "*.so", SearchOption.AllDirectories).Any()
                   || Directory.EnumerateFiles(directory, "*.dylib", SearchOption.AllDirectories).Any();
        }
        catch (IOException)
        {
            return false;
        }
    }
}
