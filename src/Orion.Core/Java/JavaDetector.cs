using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Orion.Core.Diagnostics;
using Orion.Core.Game;

namespace Orion.Core.Java;

/// <summary>A detected Java installation.</summary>
public sealed record JavaRuntime
{
    /// <summary>Full path to <c>java.exe</c>.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Java home directory (<c>...\jdk-21</c>).</summary>
    public required string Home { get; init; }

    /// <summary>Feature release, e.g. <c>21</c> for <c>21.0.4</c> and <c>8</c> for <c>1.8.0_401</c>.</summary>
    public required int MajorVersion { get; init; }

    /// <summary>Version string as reported by the runtime.</summary>
    public required string Version { get; init; }

    public string? Vendor { get; init; }
    public bool Is64Bit { get; init; }
    public string Arch { get; init; } = "x86_64";

    /// <summary>Where the runtime was discovered (JAVA_HOME, PATH, directory scan, managed ...).</summary>
    public string Source { get; init; } = "unknown";

    public string DisplayName
    {
        get
        {
            var bits = Is64Bit ? "64" : "32";
            var vendor = string.IsNullOrWhiteSpace(Vendor) ? "" : $" · {Vendor}";
            return $"Java {MajorVersion} ({Version}) [{bits}-bit]{vendor}";
        }
    }

    public override string ToString() => $"{DisplayName} @ {Home}";
}

/// <summary>
/// Discovers Java runtimes.
///
/// Sources, in order: JAVA_HOME, every PATH entry, the usual vendor install directories,
/// and runtimes managed by the launcher itself. Nothing is written and no system
/// environment variable is modified.
/// </summary>
public sealed class JavaDetector
{
    private static readonly string[] VendorDirectories =
    [
        @"Java",
        @"Eclipse Adoptium",
        @"Eclipse Foundation",
        @"Microsoft",
        @"Zulu",
        @"BellSoft",
        @"Amazon Corretto",
        @"Semeru",
        @"GraalVM",
        @"JetBrains",
        @"AdoptOpenJDK",
        @"RedHat",
        @"SapMachine",
    ];

    private readonly string _managedRuntimeDirectory;
    private readonly object _gate = new();
    private IReadOnlyList<JavaRuntime>? _cache;

    public JavaDetector(GamePaths paths) : this(paths.RuntimeDir)
    {
    }

    public JavaDetector(string managedRuntimeDirectory) => _managedRuntimeDirectory = managedRuntimeDirectory;

    public async Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!forceRefresh && _cache is not null) return _cache;
        }

        var candidates = EnumerateCandidates().ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<JavaRuntime>();

        foreach (var (home, source) in candidates)
        {
            ct.ThrowIfCancellationRequested();

            string normalized;
            try
            {
                normalized = Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!seen.Add(normalized)) continue;

            var runtime = await InspectAsync(normalized, source, ct).ConfigureAwait(false);
            if (runtime is null) continue;

            // The launcher shim in "Common Files\Oracle\Java\javapath" reports the real home.
            if (!string.Equals(runtime.Home, normalized, StringComparison.OrdinalIgnoreCase) && seen.Add(runtime.Home))
            {
                results.Add(runtime);
            }
            else
            {
                results.Add(runtime);
            }
        }

        var ordered = results
            .OrderByDescending(r => r.MajorVersion)
            .ThenByDescending(r => r.Is64Bit)
            .ThenBy(r => r.Home, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_gate)
        {
            _cache = ordered;
        }

        AppLog.Info("java", "discovered", ("count", ordered.Length), ("majors", string.Join(",", ordered.Select(r => r.MajorVersion).Distinct())));
        return ordered;
    }

    /// <summary>
    /// Picks the runtime that should run a game requiring <paramref name="requiredMajorVersion"/>.
    /// Preference: exact feature release (64-bit first), then the lowest release above the
    /// requirement (Java is backwards compatible), and never something older than required.
    /// </summary>
    public static JavaRuntime? SelectFor(int requiredMajorVersion, IEnumerable<JavaRuntime> runtimes)
    {
        var list = runtimes.ToList();
        if (list.Count == 0) return null;

        var exact = list.Where(r => r.MajorVersion == requiredMajorVersion).OrderByDescending(r => r.Is64Bit).FirstOrDefault();
        if (exact is not null) return exact;

        var newer = list.Where(r => r.MajorVersion > requiredMajorVersion)
            .OrderBy(r => r.MajorVersion)
            .ThenByDescending(r => r.Is64Bit)
            .FirstOrDefault();

        return newer;
    }

    private IEnumerable<(string Home, string Source)> EnumerateCandidates()
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) yield return (javaHome, "JAVA_HOME");

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = entry.Trim('"');
            if (directory.Length == 0) continue;

            string home;
            try
            {
                home = new DirectoryInfo(directory).Name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    ? new DirectoryInfo(directory).Parent?.FullName ?? directory
                    : directory;
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(Path.Combine(home, "bin", "java.exe"))) yield return (home, "PATH");
        }

        foreach (var root in ProgramRoots())
        {
            if (!Directory.Exists(root)) continue;

            foreach (var vendor in VendorDirectories)
            {
                var vendorPath = Path.Combine(root, vendor);
                if (!Directory.Exists(vendorPath)) continue;

                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateDirectories(vendorPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (File.Exists(Path.Combine(child, "bin", "java.exe"))) yield return (child, "scan");
                }
            }
        }

        if (Directory.Exists(_managedRuntimeDirectory))
        {
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(_managedRuntimeDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                children = [];
            }

            foreach (var child in children)
            {
                if (File.Exists(Path.Combine(child, "bin", "java.exe"))) yield return (child, "managed");
                else if (File.Exists(Path.Combine(child, "bin", "javaw.exe"))) yield return (child, "managed");
            }
        }
    }

    private static IEnumerable<string> ProgramRoots()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrWhiteSpace(programFiles)) yield return programFiles;
        if (!string.IsNullOrWhiteSpace(programFilesX86)) yield return programFilesX86;
        if (!string.IsNullOrWhiteSpace(localAppData)) yield return Path.Combine(localAppData, "Programs");
    }

    private static async Task<JavaRuntime?> InspectAsync(string home, string source, CancellationToken ct)
    {
        var exe = Path.Combine(home, "bin", "java.exe");
        if (!File.Exists(exe)) return null;

        var release = ReadReleaseFile(home);

        string? version = release.GetValueOrDefault("JAVA_VERSION");
        string? arch = release.GetValueOrDefault("OS_ARCH");
        var vendor = release.GetValueOrDefault("IMPLEMENTOR");

        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(arch))
        {
            var probe = await ProbeAsync(exe, ct).ConfigureAwait(false);
            version ??= probe.Version;
            arch ??= probe.Arch;
            vendor ??= probe.Vendor;

            if (!string.IsNullOrWhiteSpace(probe.Home) && Directory.Exists(probe.Home))
                home = probe.Home;
        }

        if (string.IsNullOrWhiteSpace(version)) return null;

        var major = ParseMajorVersion(version);
        if (major <= 0) return null;

        var archNormalized = NormalizeArch(arch);
        var is64 = archNormalized is "x86_64" or "arm64" or "aarch64";

        return new JavaRuntime
        {
            ExecutablePath = exe,
            Home = home,
            MajorVersion = major,
            Version = version,
            Vendor = string.IsNullOrWhiteSpace(vendor) ? null : vendor,
            Is64Bit = is64,
            Arch = archNormalized,
            Source = source,
        };
    }

    /// <summary>Parses <c>release</c> properties shipped in every modern JDK/JRE.</summary>
    public static Dictionary<string, string> ReadReleaseFile(string home)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(home, "release");
        if (!File.Exists(path)) return result;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var index = line.IndexOf('=');
                if (index <= 0) continue;

                var key = line[..index].Trim();
                var value = line[(index + 1)..].Trim().Trim('"');
                if (key.Length > 0) result[key] = value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    /// <summary>"1.8.0_401" → 8, "21.0.4" → 21, "17-ea" → 17.</summary>
    public static int ParseMajorVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;

        var text = version.Trim();
        if (text.StartsWith("1.", StringComparison.Ordinal))
        {
            var rest = text[2..];
            var end = rest.IndexOfAny(['.', '_', '-', '+']);
            var part = end >= 0 ? rest[..end] : rest;
            return int.TryParse(part, out var legacy) ? legacy : 0;
        }

        var first = text.Split('.', '-', '+', '_')[0];
        return int.TryParse(first, out var major) ? major : 0;
    }

    private static string NormalizeArch(string? arch) => (arch ?? "").ToLowerInvariant() switch
    {
        "amd64" or "x86_64" or "x64" or "64" => "x86_64",
        "x86" or "i386" or "i686" or "32" => "x86",
        "aarch64" or "arm64" or "armv8" => "arm64",
        "" => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        var other => other,
    };

    private static async Task<(string? Version, string? Arch, string? Vendor, string? Home)> ProbeAsync(string executable, CancellationToken ct)
    {
        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("-XshowSettings:properties");
            startInfo.ArgumentList.Add("-version");

            using var process = Process.Start(startInfo);
            if (process is null) return (null, null, null, null);

            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return (null, null, null, null);
            }

            var output = (await stderrTask.ConfigureAwait(false)) + "\n" + (await stdoutTask.ConfigureAwait(false));

            var version = Match(output, @"java\.version\s*=\s*(\S+)") ?? MatchFirstLineVersion(output);
            var arch = Match(output, @"os\.arch\s*=\s*(\S+)");
            var vendor = Match(output, @"java\.vendor\s*=\s*(.+)");
            var home = Match(output, @"java\.home\s*=\s*(.+)");

            return (version, arch, vendor, home);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, null, null, null);
        }
    }

    private static string? Match(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.Multiline, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? MatchFirstLineVersion(string text)
    {
        var match = Regex.Match(text, @"version\s+""([^""]+)""", RegexOptions.None, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups[1].Value : null;
    }
}
