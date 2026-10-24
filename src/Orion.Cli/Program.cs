using System.Diagnostics;
using System.Text;
using Orion.Core;
using Orion.Core.Auth;
using Orion.Core.Diagnostics;
using Orion.Core.Game;
using Orion.Core.Install;
using Orion.Core.Instances;
using Orion.Core.Java;
using Orion.Core.Launch;
using Orion.Core.Loaders;
using Orion.Core.Mods;
using Orion.Core.Net;

namespace Orion.Cli;

/// <summary>
/// Headless command line front end.
///
/// It is a first class part of the product (servers, CI, power users) and it is also how
/// the launcher's install/launch chain is verified without a user interface.
/// </summary>
public static class Program
{
    private static readonly CancellationTokenSource Shutdown = new();

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = Encoding.UTF8;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Shutdown.Cancel();
        };

        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var options = CommandLine.Parse(args.Skip(1).ToArray());

        try
        {
            return command switch
            {
                "java" => await JavaAsync(options).ConfigureAwait(false),
                "manifest" => await ManifestAsync(options).ConfigureAwait(false),
                "install" => await InstallAsync(options).ConfigureAwait(false),
                "launch" => await LaunchAsync(options).ConfigureAwait(false),
                "instances" => await InstancesAsync(options).ConfigureAwait(false),
                "import" => await ImportAsync(options).ConfigureAwait(false),
                "accounts" => await AccountsAsync(options).ConfigureAwait(false),
                "loaders" => await LoadersAsync(options).ConfigureAwait(false),
                "install-loader" => await InstallLoaderAsync(options).ConfigureAwait(false),
                "mods" => await ModsAsync(options).ConfigureAwait(false),
                "verify" => await VerifyAsync(options).ConfigureAwait(false),
                "pack-import" => await PackImportAsync(options).ConfigureAwait(false),
                "pack-export" => await PackExportAsync(options).ConfigureAwait(false),
                "selftest" => await SelfTestAsync(options).ConfigureAwait(false),
                _ => Unknown(command),
            };
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("已取消。");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.Message}");
            AppLog.Error("cli", "unhandled", ex, ("command", command));
            if (options.Has("--verbose")) Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Orap Craft Launcher — 命令行工具

            用法: orap <命令> [选项]

            命令:
              java                              列出检测到的 Java 运行时
              manifest [--refresh] [--type all] 显示官方版本清单
              install <版本>                    下载并安装一个 Minecraft 版本
              launch  <版本|实例>               安装（如需要）并启动游戏
              instances [--create <名称> <版本>] [--remove <实例ID>]
              import  <目录>                    导入 .minecraft / Prism 实例
              accounts [--add-offline <名称>] [--list]
              selftest                          运行离线自检

            通用选项:
              --root <目录>      数据根目录（默认 %APPDATA%\OrapCraftLauncher 或 portable.txt 旁的 data）
              --mirror           使用 BMCLAPI 镜像
              --concurrency <n>  下载并发数
              --proxy <url>      代理，例如 http://127.0.0.1:7890
              --verbose          输出详细错误

            launch 选项:
              --offline <名称>   使用离线账户启动
              --memory <MB>      最大内存
              --timeout <秒>     等待游戏退出的最长时间
              --kill-after <秒>  启动后强制结束（自动化测试用）
            """);
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令: {command}。运行 “orion help” 查看用法。");
        return 2;
    }

    private static LauncherContext CreateContext(CommandLine options)
    {
        var context = LauncherContext.Create(options.GetString("--root"));

        if (options.Has("--mirror")) context.Network.Source = DownloadSource.BmclApi;
        if (options.GetInt("--concurrency") is { } concurrency) context.DownloadEngineOptions.MaxConcurrency = concurrency;

        return context;
    }

    private static async Task<int> JavaAsync(CommandLine options)
    {
        await using var context = CreateContext(options);
        var runtimes = await context.JavaDetector.DiscoverAsync(forceRefresh: true).ConfigureAwait(false);

        if (runtimes.Count == 0)
        {
            Console.WriteLine("未检测到任何 Java 运行时。");
            return 1;
        }

        Console.WriteLine($"检测到 {runtimes.Count} 个 Java 运行时:");
        foreach (var runtime in runtimes)
        {
            Console.WriteLine($"  [{runtime.MajorVersion,3}] {runtime.DisplayName}");
            Console.WriteLine($"        {runtime.ExecutablePath}  ({runtime.Source})");
        }

        Console.WriteLine();
        Console.WriteLine($"Java 21 → {JavaDetector.SelectFor(21, runtimes)?.Home ?? "未找到"}");
        Console.WriteLine($"Java 17 → {JavaDetector.SelectFor(17, runtimes)?.Home ?? "未找到"}");
        Console.WriteLine($"Java 8  → {JavaDetector.SelectFor(8, runtimes)?.Home ?? "未找到"}");
        return 0;
    }

    private static async Task<int> ManifestAsync(CommandLine options)
    {
        await using var context = CreateContext(options);

        var started = Stopwatch.StartNew();
        var manifest = await context.VersionRepository.GetManifestAsync(options.Has("--refresh"), Shutdown.Token).ConfigureAwait(false);

        Console.WriteLine($"最新正式版: {manifest.Latest?.Release}   最新快照: {manifest.Latest?.Snapshot}");
        Console.WriteLine($"版本总数: {manifest.Versions.Count}  (来源: {string.Join(", ", context.VersionRepository.ManifestUrls.Select(u => u.Host))}, {started.ElapsedMilliseconds} ms)");

        var type = options.GetString("--type") ?? "release";
        var limit = options.GetInt("--limit") ?? 10;

        var filtered = type switch
        {
            "all" => manifest.Versions,
            "snapshot" => manifest.Versions.Where(v => v.Type is "snapshot").ToList(),
            _ => manifest.Versions.Where(v => v.Type is "release").ToList(),
        };

        Console.WriteLine();
        Console.WriteLine($"{(type == "all" ? "全部" : type)} 版本 (显示前 {limit} 个，另有历史版本 {manifest.Versions.Count - filtered.Count}):");
        foreach (var version in filtered.Take(limit))
        {
            Console.WriteLine($"  {version.Id,-22} {version.Type,-10} {version.ReleaseTime:yyyy-MM-dd}");
        }

        var old = manifest.Versions.Where(v => v.Type is not ("release" or "snapshot")).ToList();
        if (old.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"历史版本 (old_alpha / old_beta) 共 {old.Count} 个，例如: {string.Join(", ", old.Take(5).Select(v => v.Id))}");
        }

        return 0;
    }

    private static async Task<int> InstallAsync(CommandLine options)
    {
        var versionId = options.Positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(versionId))
        {
            Console.Error.WriteLine("用法: orap install <版本>");
            return 2;
        }

        await using var context = CreateContext(options);
        await context.InitializeAsync(Shutdown.Token).ConfigureAwait(false);

        Console.WriteLine($"开始安装 {versionId} → {context.Paths.Root}");

        var lastReport = DateTime.UtcNow;
        var progress = new Progress<InstallProgress>(p =>
        {
            if (DateTime.UtcNow - lastReport < TimeSpan.FromMilliseconds(400) && p.Phase != InstallPhase.Completed) return;
            lastReport = DateTime.UtcNow;

            var speed = p.Download is null ? "" : $" · {SpeedMeter.Humanize(p.Download.BytesPerSecond)}";
            var files = p.Download is null ? "" : $" · {p.Download.CompletedFiles}/{p.Download.TotalFiles} 文件";
            Console.WriteLine($"  [{p.Percent,5:0.0}%] {p.Phase,-12} {p.Message}{files}{speed}");
        });

        var result = await context.InstallService.InstallAsync(
            versionId,
            new InstallOptions { InstallAssets = !options.Has("--no-assets") },
            progress,
            Shutdown.Token).ConfigureAwait(false);

        if (!result.Success)
        {
            Console.Error.WriteLine($"安装失败: {result.Error}");
            foreach (var failure in result.Failures.Take(10))
            {
                Console.Error.WriteLine($"  - {failure.TargetPath}: {failure.Message} ({failure.Url})");
            }

            return 1;
        }

        Console.WriteLine($"安装完成: {result.FilesDownloaded} 个文件, {result.BytesDownloaded / 1024.0 / 1024.0:0.0} MB, 用时 {result.Duration.TotalSeconds:0.0}s");
        foreach (var warning in result.Warnings.Take(10)) Console.WriteLine("  警告: " + warning);

        var resolved = await context.VersionResolver.ResolveAsync(versionId, Shutdown.Token).ConfigureAwait(false);
        Console.WriteLine($"  根版本: {resolved.RootVersionId} · 需要的 Java: {resolved.RequiredJavaMajorVersion} · 依赖库: {resolved.Metadata.Libraries?.Count ?? 0}");

        return 0;
    }

    private static async Task<int> LaunchAsync(CommandLine options)
    {
        var target = options.Positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine("用法: orap launch <版本|实例ID> [--offline 名称]");
            return 2;
        }

        await using var context = CreateContext(options);
        await context.InitializeAsync(Shutdown.Token).ConfigureAwait(false);

        // Resolve instance first, falling back to a bare version id.
        var instances = context.Instances.LoadAll();
        var instance = instances.FirstOrDefault(i => string.Equals(i.Id, target, StringComparison.OrdinalIgnoreCase))
                       ?? instances.FirstOrDefault(i => string.Equals(i.VersionId, target, StringComparison.OrdinalIgnoreCase));

        var versionId = instance?.VersionId ?? target;
        var gameDirectory = instance?.GameDirectory ?? Path.Combine(context.Paths.Root, "versions", versionId);

        Console.WriteLine($"解析版本 {versionId} …");
        ResolvedVersion resolved;
        try
        {
            resolved = await context.VersionResolver.ResolveAsync(versionId, Shutdown.Token).ConfigureAwait(false);
        }
        catch (VersionNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine($"  继承链: {string.Join(" <- ", resolved.Chain)} · Java {resolved.RequiredJavaMajorVersion}");

        // Install automatically when the version is not complete yet.
        var clientJar = context.Paths.VersionJarPath(resolved.ClientJarVersionId);
        if (!File.Exists(clientJar))
        {
            Console.WriteLine("客户端未安装，正在安装…");
            var installResult = await context.InstallService.InstallAsync(versionId, new InstallOptions(), null, Shutdown.Token).ConfigureAwait(false);
            if (!installResult.Success)
            {
                Console.Error.WriteLine("安装失败: " + installResult.Error);
                return 1;
            }

            resolved = await context.VersionResolver.ResolveAsync(versionId, Shutdown.Token).ConfigureAwait(false);
        }

        var runtimes = await context.JavaDetector.DiscoverAsync().ConfigureAwait(false);
        var java = !string.IsNullOrWhiteSpace(instance?.JavaPath) && File.Exists(instance.JavaPath)
            ? runtimes.FirstOrDefault(r => string.Equals(r.Home, Path.GetDirectoryName(Path.GetDirectoryName(instance.JavaPath)), StringComparison.OrdinalIgnoreCase))
              ?? JavaDetector.SelectFor(resolved.RequiredJavaMajorVersion, runtimes)
            : JavaDetector.SelectFor(resolved.RequiredJavaMajorVersion, runtimes);

        if (java is null)
        {
            Console.Error.WriteLine($"未找到可用的 Java {resolved.RequiredJavaMajorVersion}。请先安装对应的 Java 运行时。");
            return 1;
        }

        Console.WriteLine($"  Java: {java.DisplayName} @ {java.Home}");

        var offlineName = options.GetString("--offline");
        Account account;
        if (offlineName is not null)
        {
            if (!OfflineAccounts.IsValidName(offlineName))
            {
                Console.Error.WriteLine("离线用户名需为 3-16 位字母、数字或下划线。");
                return 2;
            }

            account = OfflineAccounts.Create(offlineName);
            Console.WriteLine($"  账户: 离线模式 ({account.Username}, UUID {account.Uuid})");
        }
        else
        {
            account = await context.GetActiveAccountAsync(Shutdown.Token).ConfigureAwait(false);
            Console.WriteLine($"  账户: {account.Username} ({account.Type})");
        }

        var memory = options.GetInt("--memory") ?? instance?.EffectiveMemory ?? context.Settings.MemoryMb;

        var request = new LaunchRequest
        {
            Version = resolved,
            Account = account,
            Java = java,
            GameDirectory = gameDirectory,
            MemoryMb = memory,
            MinMemoryMb = instance?.MinMemoryMb ?? context.Settings.MinMemoryMb,
            ExtraJvmArguments = options.GetString("--jvm") ?? instance?.JvmArguments ?? context.Settings.JvmArguments,
            ExtraGameArguments = instance?.GameArguments,
            WindowWidth = instance?.WindowWidth ?? 854,
            WindowHeight = instance?.WindowHeight ?? 480,
            Fullscreen = instance?.Fullscreen ?? false,
            LauncherVersion = "0.1.0",
        };

        var logProgress = new Progress<string>(line => Console.WriteLine("  [启动] " + line));

        GameSession session;
        try
        {
            session = await context.Launcher.LaunchAsync(request, logProgress, Shutdown.Token).ConfigureAwait(false);
        }
        catch (LaunchException ex)
        {
            Console.Error.WriteLine("启动失败: " + ex.Message);
            return 1;
        }

        Console.WriteLine($"游戏已启动 (PID {session.ProcessId})，日志: {session.LogFilePath}");

        // Live tail of the game log until it exits (or until we are told to stop).
        var tail = Task.Run(async () =>
        {
            var index = 0;
            while (!session.HasExited && !Shutdown.IsCancellationRequested)
            {
                await Task.Delay(500).ConfigureAwait(false);
                var lines = session.RecentLines;
                while (index < lines.Count)
                {
                    Console.WriteLine("  | " + lines[index++]);
                }
            }
        });

        var killAfter = options.GetInt("--kill-after");
        if (killAfter is > 0)
        {
            Console.WriteLine($"  {killAfter} 秒后自动结束游戏进程（测试模式）。");
            await Task.Delay(TimeSpan.FromSeconds(killAfter.Value)).ConfigureAwait(false);
            session.Kill();
        }

        var timeout = options.GetInt("--timeout") ?? 0;
        int exitCode;
        if (timeout > 0)
        {
            var completed = await Task.WhenAny(session.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(timeout))).ConfigureAwait(false);
            if (completed is Task<int> exitTask) exitCode = await exitTask.ConfigureAwait(false);
            else
            {
                Console.WriteLine($"  超过 {timeout} 秒仍未退出，强制结束。");
                session.Kill();
                exitCode = await session.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        else
        {
            exitCode = await session.WaitForExitAsync().ConfigureAwait(false);
        }

        await tail.ConfigureAwait(false);

        Console.WriteLine($"游戏进程已退出，退出代码 {exitCode}");

        if (session.KilledByLauncher)
        {
            Console.WriteLine("（进程由启动器主动结束，跳过崩溃分析）");
        }
        else
        {
            var analysis = await CrashAnalyzer.AnalyzeAsync(gameDirectory, exitCode, session.RecentLines, Shutdown.Token).ConfigureAwait(false);
            if (analysis.IsCrash)
            {
                Console.WriteLine("崩溃分析:");
                Console.WriteLine("  结论: " + analysis.Summary);
                foreach (var cause in analysis.Causes.Distinct().Skip(1)) Console.WriteLine("  - " + cause);
                foreach (var evidence in analysis.Evidence.Take(6)) Console.WriteLine("  证据: " + evidence);
                if (analysis.CrashReportPath is not null) Console.WriteLine("  崩溃报告: " + analysis.CrashReportPath);
            }
        }

        await session.DisposeAsync().ConfigureAwait(false);

        if (instance is not null)
        {
            instance.LastPlayedAt = DateTimeOffset.Now;
            await context.Instances.SaveAsync(instance).ConfigureAwait(false);
        }

        return session.KilledByLauncher ? 0 : (exitCode == 0 ? 0 : 1);
    }

    private static async Task<int> InstancesAsync(CommandLine options)
    {
        await using var context = CreateContext(options);
        await context.InitializeAsync(Shutdown.Token).ConfigureAwait(false);

        if (options.Has("--create"))
        {
            var name = options.GetString("--create");
            var version = options.Positional.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
            {
                Console.Error.WriteLine("用法: orap instances --create <名称> <版本>");
                return 2;
            }

            var created = await context.Instances.CreateAsync(name, version, version, LoaderKind.Vanilla, null, Shutdown.Token).ConfigureAwait(false);
            Console.WriteLine($"已创建实例 {created.Id}: {created.Name} ({created.VersionId})");
            Console.WriteLine($"  游戏目录: {created.GameDirectory}");
            return 0;
        }

        var remove = options.GetString("--remove");
        if (!string.IsNullOrWhiteSpace(remove))
        {
            var instance = context.Instances.LoadAll().FirstOrDefault(i => string.Equals(i.Id, remove, StringComparison.OrdinalIgnoreCase));
            if (instance is null)
            {
                Console.Error.WriteLine("未找到实例: " + remove);
                return 1;
            }

            var hasData = InstanceManager.HasUserData(instance.GameDirectory);
            var trash = context.Instances.Remove(instance, out var trashPath);
            Console.WriteLine($"已移除实例 {instance.Id}。");
            if (trashPath is not null) Console.WriteLine($"  文件已移动到: {trashPath}（未删除，可手动恢复）");
            if (hasData) Console.WriteLine("  注意: 该实例包含存档或 Mod，已完整保留在回收目录。");
            return 0;
        }

        var instances = context.Instances.LoadAll();
        if (instances.Count == 0)
        {
            Console.WriteLine($"没有实例。数据目录: {context.Paths.Root}");
            return 0;
        }

        Console.WriteLine($"共 {instances.Count} 个实例:");
        foreach (var instance in instances)
        {
            var external = instance.IsExternal ? "  [外部导入]" : "";
            Console.WriteLine($"  {instance.Id,-32} {instance.Name} — {instance.Subtitle}{external}");
            Console.WriteLine($"      目录: {instance.GameDirectory}");
        }

        return 0;
    }

    private static async Task<int> ImportAsync(CommandLine options)
    {
        var path = options.Positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine("用法: orap import <目录>");
            return 2;
        }

        await using var context = CreateContext(options);
        var result = await context.Importer.ImportAsync(path, Shutdown.Token).ConfigureAwait(false);

        Console.WriteLine($"识别为: {result.DetectedSource}");
        foreach (var instance in result.Instances)
        {
            Console.WriteLine($"  导入: {instance.Name} ({instance.VersionId}) → {instance.GameDirectory}");
        }

        foreach (var warning in result.Warnings) Console.WriteLine("  警告: " + warning);

        return result.Success ? 0 : 1;
    }

    private static async Task<int> AccountsAsync(CommandLine options)
    {
        await using var context = CreateContext(options);

        var addOffline = options.GetString("--add-offline");
        if (addOffline is not null)
        {
            if (!OfflineAccounts.IsValidName(addOffline))
            {
                Console.Error.WriteLine("离线用户名需为 3-16 位字母、数字或下划线。");
                return 2;
            }

            var accounts = (await context.AccountStore.LoadAsync(Shutdown.Token).ConfigureAwait(false)).ToList();
            if (accounts.Any(a => string.Equals(a.Username, addOffline, StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine("该离线账户已存在。");
                return 1;
            }

            var account = OfflineAccounts.Create(addOffline);
            accounts.Add(account);
            await context.AccountStore.SaveAsync(accounts, Shutdown.Token).ConfigureAwait(false);
            Console.WriteLine($"已添加离线账户 {account.Username} (UUID {account.Uuid})");
            return 0;
        }

        var all = await context.AccountStore.LoadAsync(Shutdown.Token).ConfigureAwait(false);
        if (all.Count == 0)
        {
            Console.WriteLine("没有已保存的账户。");
            return 0;
        }

        Console.WriteLine($"共 {all.Count} 个账户（令牌经 DPAPI 加密存储）:");
        foreach (var account in all)
        {
            var tokenState = account.Type == AccountType.Microsoft
                ? (account.IsExpired ? "需要刷新" : "有效")
                : "离线";
            Console.WriteLine($"  {account.Username,-16} {account.Type,-10} {tokenState}");
        }

        return 0;
    }

    private static LoaderKind? ParseLoaderKind(string? text) => text?.ToLowerInvariant() switch
    {
        "neoforge" or "neo" => LoaderKind.NeoForge,
        "forge" => LoaderKind.Forge,
        "fabric" => LoaderKind.Fabric,
        "quilt" => LoaderKind.Quilt,
        "vanilla" or "official" => LoaderKind.Vanilla,
        _ => null,
    };

    private static async Task<int> LoadersAsync(CommandLine options)
    {
        var kind = ParseLoaderKind(options.Positional.ElementAtOrDefault(0));
        var minecraftVersion = options.Positional.ElementAtOrDefault(1) ?? "1.21.1";

        if (kind is null or LoaderKind.Vanilla)
        {
            Console.Error.WriteLine("用法: orap loaders <neoforge|forge|fabric|quilt> <Minecraft版本>");
            return 2;
        }

        await using var context = CreateContext(options);

        Console.WriteLine($"正在获取 {LoaderService.DisplayName(kind.Value)} 适用于 {minecraftVersion} 的版本…");
        var versions = await context.Loaders.GetVersionsAsync(kind.Value, minecraftVersion, Shutdown.Token).ConfigureAwait(false);

        if (versions.Count == 0)
        {
            Console.WriteLine("没有找到可用的加载器版本。");
            return 1;
        }

        Console.WriteLine($"共 {versions.Count} 个版本（显示前 12 个）:");
        foreach (var version in versions.Take(12))
        {
            Console.WriteLine($"  {version.Version}{(version.Stable ? "  (稳定)" : "")}");
        }

        Console.WriteLine();
        Console.WriteLine($"最新: {versions.First().Version}");
        return 0;
    }

    private static async Task<int> InstallLoaderAsync(CommandLine options)
    {
        var kind = ParseLoaderKind(options.Positional.ElementAtOrDefault(0));
        var minecraftVersion = options.Positional.ElementAtOrDefault(1) ?? "1.21.1";

        if (kind is null or LoaderKind.Vanilla)
        {
            Console.Error.WriteLine("用法: orap install-loader <neoforge|forge|fabric|quilt> <Minecraft版本> [--loader 版本] [--name 实例名]");
            return 2;
        }

        await using var context = CreateContext(options);
        await context.InitializeAsync(Shutdown.Token).ConfigureAwait(false);

        var loaderVersion = options.GetString("--loader");
        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            var versions = await context.Loaders.GetVersionsAsync(kind.Value, minecraftVersion, Shutdown.Token).ConfigureAwait(false);
            if (versions.Count == 0)
            {
                Console.Error.WriteLine("没有可用的加载器版本。");
                return 1;
            }

            loaderVersion = versions.First().Version;
        }

        Console.WriteLine($"安装 {LoaderService.DisplayName(kind.Value)} {loaderVersion} (Minecraft {minecraftVersion}) → {context.Paths.Root}");

        var runtimes = await context.JavaDetector.DiscoverAsync().ConfigureAwait(false);
        var requiredJava = kind.Value is LoaderKind.NeoForge or LoaderKind.Forge ? 21 : 17;
        var java = JavaDetector.SelectFor(requiredJava, runtimes) ?? runtimes.FirstOrDefault();

        var progress = new Progress<string>(line => Console.WriteLine("  [安装] " + line));
        var result = await context.Loaders.InstallAsync(
            kind.Value,
            new LoaderInstallRequest
            {
                MinecraftVersion = minecraftVersion,
                LoaderVersion = loaderVersion,
                Java = java,
                InstallerMirror = context.Network.Source == DownloadSource.BmclApi ? "https://bmclapi2.bangbang93.com/maven/" : null,
                Output = progress,
            },
            Shutdown.Token).ConfigureAwait(false);

        if (!result.Success)
        {
            Console.Error.WriteLine("安装失败: " + result.Error);
            foreach (var warning in result.Warnings) Console.WriteLine("  警告: " + warning);
            return 1;
        }

        Console.WriteLine($"安装成功: 版本 ID = {result.VersionId}");
        foreach (var warning in result.Warnings.Take(10)) Console.WriteLine("  警告: " + warning);

        var instanceName = options.GetString("--name") ?? $"{minecraftVersion} {LoaderService.DisplayName(kind.Value)}";
        var instance = await context.Instances.CreateAsync(instanceName, result.VersionId, minecraftVersion, kind.Value, loaderVersion, Shutdown.Token).ConfigureAwait(false);
        Console.WriteLine($"已创建实例: {instance.Id}");
        Console.WriteLine($"  游戏目录: {instance.GameDirectory}");
        Console.WriteLine();
        Console.WriteLine($"启动命令: orion launch {instance.Id} --offline Tester");

        return 0;
    }

    private static async Task<int> ModsAsync(CommandLine options)
    {
        var target = options.Positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine("用法: orap mods <实例ID|游戏目录>");
            return 2;
        }

        await using var context = CreateContext(options);

        string gameDirectory;
        var instance = context.Instances.LoadAll().FirstOrDefault(i => string.Equals(i.Id, target, StringComparison.OrdinalIgnoreCase));
        if (instance is not null) gameDirectory = instance.GameDirectory;
        else if (Directory.Exists(target)) gameDirectory = Path.GetFullPath(target);
        else
        {
            Console.Error.WriteLine("未找到该实例或目录。");
            return 1;
        }

        var search = options.GetString("--search");
        if (search is not null)
        {
            var result = await context.Modrinth.SearchAsync(new ModrinthSearchQuery
            {
                Query = search,
                GameVersion = instance?.GameVersion,
                Loader = instance?.Loader,
                Limit = options.GetInt("--limit") ?? 10,
            }, Shutdown.Token).ConfigureAwait(false);

            Console.WriteLine($"Modrinth 搜索 “{search}”: 共 {result.TotalHits} 个结果");
            foreach (var project in result.Hits)
            {
                Console.WriteLine($"  {project.Title} ({project.Slug}) — {project.Downloads:N0} 次下载");
                Console.WriteLine($"      {Truncate(project.Description, 110)}");
            }

            return 0;
        }

        var mods = await ModScanner.ScanAsync(gameDirectory, Shutdown.Token).ConfigureAwait(false);
        Console.WriteLine($"目录 {Path.Combine(gameDirectory, "mods")} 中共 {mods.Count} 个 Mod:");
        foreach (var mod in mods)
        {
            var state = mod.Enabled ? "启用" : "禁用";
            Console.WriteLine($"  [{state}] {mod.DisplayName} {mod.Version} ({mod.Loader ?? "unknown"}) {mod.Size / 1024} KB");
            if (mod.Dependencies.Count > 0) Console.WriteLine($"      依赖: {string.Join(", ", mod.Dependencies)}");
        }

        if (instance is not null)
        {
            var incompatible = mods.Where(m => !ModScanner.IsCompatibleWith(m, instance.Loader)).ToList();
            if (incompatible.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"注意: {incompatible.Count} 个 Mod 与实例加载器 ({instance.DisplayLoader}) 不匹配:");
                foreach (var mod in incompatible) Console.WriteLine($"  - {mod.DisplayName} ({mod.Loader})");
            }
        }

        return 0;
    }

    private static string Truncate(string? text, int length)
        => string.IsNullOrEmpty(text) ? "" : text.Length <= length ? text : text[..length] + "…";

    private static async Task<int> VerifyAsync(CommandLine options)
    {
        var target = options.Positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine("用法: orap verify <版本|实例ID> [--repair]");
            return 2;
        }

        await using var context = CreateContext(options);
        var instance = context.Instances.LoadAll().FirstOrDefault(i => string.Equals(i.Id, target, StringComparison.OrdinalIgnoreCase));
        var versionId = instance?.VersionId ?? target;

        Console.WriteLine($"校验 {versionId} …");
        var progress = new Progress<VerifyProgress>(p => Console.WriteLine($"  [{p.Percent,5:0.0}%] {p.Message}"));

        var result = await context.Integrity.VerifyAsync(versionId, context.Mirrors.Resolve, progress, deep: options.Has("--deep"), ct: Shutdown.Token).ConfigureAwait(false);

        Console.WriteLine($"已校验 {result.CheckedFiles} 个文件（含 {result.AssetCount} 个资源）· 用时 {result.Duration.TotalSeconds:0.0}s");
        Console.WriteLine($"  缺失: {result.Missing.Count}   损坏: {result.Corrupt.Count}");

        foreach (var path in result.Missing.Take(5)) Console.WriteLine("  缺失: " + path);
        foreach (var path in result.Corrupt.Take(5)) Console.WriteLine("  损坏: " + path);

        if (result.IsHealthy)
        {
            Console.WriteLine("完整性校验通过。");
            return 0;
        }

        if (!options.Has("--repair")) return 1;

        Console.WriteLine("正在修复…");
        var repair = await context.Integrity.RepairAsync(versionId, context.Mirrors.Resolve, progress, Shutdown.Token).ConfigureAwait(false);
        Console.WriteLine(repair.Success
            ? $"修复完成: {repair.FilesDownloaded} 个文件, {repair.BytesDownloaded / 1024.0 / 1024.0:0.0} MB"
            : "修复失败: " + repair.Error);

        return repair.Success ? 0 : 1;
    }

    private static async Task<int> PackImportAsync(CommandLine options)
    {
        var path = options.Positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Console.Error.WriteLine("用法: orap pack-import <文件.mrpack> [--name 实例名]");
            return 2;
        }

        await using var context = CreateContext(options);
        await context.InitializeAsync(Shutdown.Token).ConfigureAwait(false);

        var index = await MrPackInstaller.ReadIndexAsync(path, Shutdown.Token).ConfigureAwait(false);
        Console.WriteLine(index is null
            ? "无法读取整合包索引。"
            : $"整合包: {index.Name} {index.VersionId} · 依赖: {string.Join(", ", index.Dependencies.Select(d => $"{d.Key}={d.Value}"))}");

        var runtimes = await context.JavaDetector.DiscoverAsync().ConfigureAwait(false);
        var java = JavaDetector.SelectFor(21, runtimes) ?? runtimes.FirstOrDefault();

        var output = new Progress<string>(line => Console.WriteLine("  [整合包] " + line));
        var result = await context.ModPacks.InstallAsync(path, options.GetString("--name"), java, output, Shutdown.Token).ConfigureAwait(false);

        if (!result.Success)
        {
            Console.Error.WriteLine("导入失败: " + result.Error);
            return 1;
        }

        Console.WriteLine($"导入完成: 实例 {result.InstanceId}, 版本 {result.VersionId}, 文件 {result.FilesDownloaded}, 覆盖 {result.OverridesCopied}");
        foreach (var warning in result.Warnings.Take(5)) Console.WriteLine("  警告: " + warning);
        return 0;
    }

    private static async Task<int> PackExportAsync(CommandLine options)
    {
        var instanceId = options.Positional.ElementAtOrDefault(0);
        var output = options.Positional.ElementAtOrDefault(1) ?? "instance.mrpack";

        if (string.IsNullOrWhiteSpace(instanceId))
        {
            Console.Error.WriteLine("用法: orap pack-export <实例ID> <输出.mrpack> [--saves]");
            return 2;
        }

        await using var context = CreateContext(options);
        var instance = context.Instances.LoadAll().FirstOrDefault(i => string.Equals(i.Id, instanceId, StringComparison.OrdinalIgnoreCase));
        if (instance is null)
        {
            Console.Error.WriteLine("未找到实例: " + instanceId);
            return 1;
        }

        var result = await MrPackExporter.ExportAsync(instance, output, new MrPackExporter.ExportOptions
        {
            Name = instance.Name,
            IncludeSaves = options.Has("--saves"),
        }, Shutdown.Token).ConfigureAwait(false);

        if (!result.Success)
        {
            Console.Error.WriteLine("导出失败: " + result.Error);
            return 1;
        }

        Console.WriteLine($"已导出 {result.OverridesCopied} 个文件到 {Path.GetFullPath(output)}");
        return 0;
    }

    private static async Task<int> SelfTestAsync(CommandLine options)
    {
        await using var context = CreateContext(options);
        await context.InitializeAsync(Shutdown.Token).ConfigureAwait(false);

        Console.WriteLine("Orion 自检");
        Console.WriteLine($"  数据目录: {context.Paths.Root}");
        Console.WriteLine($"  日志目录: {AppLog.LogDirectory}");
        Console.WriteLine($"  下载源:   {context.Network.Source} → {string.Join(", ", context.Mirrors.Resolve(new Uri("https://piston-meta.mojang.com/v1/packages/x.json")).Select(u => u.Host))}");

        var runtimes = await context.JavaDetector.DiscoverAsync().ConfigureAwait(false);
        Console.WriteLine($"  Java:     检测到 {runtimes.Count} 个运行时");

        var manifest = await context.VersionRepository.GetManifestAsync(false, Shutdown.Token).ConfigureAwait(false);
        Console.WriteLine($"  版本清单: {manifest.Versions.Count} 个版本, 最新 {manifest.Latest?.Release}");

        var instances = context.Instances.LoadAll();
        Console.WriteLine($"  实例:     {instances.Count} 个");

        var free = new DriveInfo(Path.GetPathRoot(context.Paths.Root)!).AvailableFreeSpace;
        Console.WriteLine($"  磁盘剩余: {free / 1024.0 / 1024 / 1024:0.0} GB");

        Console.WriteLine("自检完成。");
        return 0;
    }
}

/// <summary>Tiny argument parser: <c>--key value</c>, <c>--flag</c>, and positionals.</summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = [];

    public static CommandLine Parse(string[] args)
    {
        var parsed = new CommandLine();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = arg.IndexOf('=');
                if (equals > 0)
                {
                    parsed._options[arg[..equals]] = arg[(equals + 1)..];
                    continue;
                }

                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    parsed._options[arg] = args[i + 1];
                    i++;
                }
                else
                {
                    parsed._options[arg] = null;
                }

                continue;
            }

            parsed.Positional.Add(arg);
        }

        return parsed;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? GetString(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public int? GetInt(string name)
        => _options.TryGetValue(name, out var value) && int.TryParse(value, out var number) ? number : null;
}
