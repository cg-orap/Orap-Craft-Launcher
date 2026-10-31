using System.Text.Json.Serialization;
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
using Orion.Core.Settings;

namespace Orion.Core;

/// <summary>
/// Composition root: builds every service once and shares them between the GUI and the
/// headless CLI. Keeping this in the core library (not in the WPF project) is what makes
/// the launch chain testable without a user interface.
/// </summary>
public sealed class LauncherContext : IAsyncDisposable
{
    private LauncherContext(
        GamePaths paths,
        SettingsStore settingsStore,
        LauncherSettings settings,
        HttpClient http,
        NetworkOptions network,
        MirrorResolver mirrors)
    {
        Paths = paths;
        SettingsStore = settingsStore;
        Settings = settings;
        Http = http;
        Network = network;
        Mirrors = mirrors;

        DownloadEngineOptions = new DownloadEngineOptions
        {
            MaxConcurrency = Math.Clamp(settings.MaxConcurrency, 1, 32),
            FailureReportDirectory = paths.LogsDir,
        };

        DownloadEngine = new DownloadEngine(http, DownloadEngineOptions, mirrors);
        VersionRepository = new VersionRepository(paths, http, mirrors);
        VersionResolver = new VersionResolver(VersionRepository);
        InstallService = new VersionInstallService(paths, VersionRepository, VersionResolver, DownloadEngine, mirrors);
        JavaDetector = new JavaDetector(paths);
        AccountStore = new AccountStore(paths.AccountsFile);

        var authOptions = new MicrosoftAuthOptions();
        if (!string.IsNullOrWhiteSpace(settings.MicrosoftClientId)) authOptions.ClientId = settings.MicrosoftClientId;
        Auth = new MicrosoftAuthService(http, authOptions);

        Instances = new InstanceManager(paths);
        Importer = new InstanceImporter(Instances, paths);
        Launcher = new GameLauncher(paths, settings.ForceUtf8Console);
        Loaders = new LoaderService(http, paths, VersionRepository, InstallService, mirrors, DownloadEngine);
        Modrinth = new ModrinthClient(http, new Mods.ModrinthOptions(), DownloadEngine, mirrors);
        Integrity = new IntegrityService(paths, VersionRepository, VersionResolver, DownloadEngine, mirrors);
        ModPacks = new Mods.MrPackInstaller(paths, Instances, Loaders, DownloadEngine, mirrors, InstallService);

        // Declared but never constructed: the CurseForge/zip dispatch threw a
        // NullReferenceException the first time it ran.
        // CurseForge：用户填的 Key 优先，其次用编译期内置的分发 Key（见 CurseForgeDefaults）
        var curseForgeKey = Mods.CurseForgeDefaults.Resolve(settings.CurseForgeApiKey);
        CurseForge = new Mods.CurseForgeClient(http, curseForgeKey);
        Translator = new Translation.TranslationService(http);
        DependencyRepair = new Mods.ModDependencyRepair(CurseForge, http);
        PackArchives = new Mods.PackArchiveInstaller(paths, Instances, Loaders, Importer, http, curseForgeKey,
            DependencyRepair);
        
    }

    public GamePaths Paths { get; }
    public LauncherSettings Settings { get; }
    public SettingsStore SettingsStore { get; }
    public HttpClient Http { get; }
    public NetworkOptions Network { get; }
    public MirrorResolver Mirrors { get; }

    public DownloadEngineOptions DownloadEngineOptions { get; }
    public DownloadEngine DownloadEngine { get; }
    public VersionRepository VersionRepository { get; }
    public VersionResolver VersionResolver { get; }
    public VersionInstallService InstallService { get; }
    public JavaDetector JavaDetector { get; }
    public AccountStore AccountStore { get; }
    public MicrosoftAuthService Auth { get; }
    public InstanceManager Instances { get; }
    public InstanceImporter Importer { get; }
    public GameLauncher Launcher { get; }
    public LoaderService Loaders { get; }
    public ModrinthClient Modrinth { get; }
    public IntegrityService Integrity { get; }
    public MrPackInstaller ModPacks { get; }

    /// <summary>CurseForge / plain-zip modpack importer (drag &amp; drop).</summary>
    public PackArchiveInstaller PackArchives { get; }

    /// <summary>CurseForge 整合包浏览/下载（没有 Key 时 HasKey 为 false）。</summary>
    public CurseForgeClient CurseForge { get; }

    /// <summary>英文 → 简体中文的机器翻译（MyMemory 翻译记忆库，非大模型）。</summary>
    public Translation.TranslationService Translator { get; }

    /// <summary>缺前置检测与自动补齐（安装整合包后自动跑一次，模组页也能手动触发）。</summary>
    public Mods.ModDependencyRepair DependencyRepair { get; }

    /// <summary>Set once <see cref="InitializeAsync"/> has run.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Creates the context. <paramref name="rootOverride"/> wins over the stored setting and
    /// the default location, which is what the CLI <c>--root</c> flag uses.
    /// </summary>
    public static LauncherContext Create(string? rootOverride = null)
    {
        var defaultRoot = rootOverride ?? DefaultRootDirectory();
        var paths = new GamePaths(defaultRoot);
        paths.EnsureBaseDirectories();

        var settingsStore = new SettingsStore(paths);
        var settings = settingsStore.Load();

        var effectiveRoot = rootOverride ?? settings.RootDirectory;
        if (!string.IsNullOrWhiteSpace(effectiveRoot) && !Path.GetFullPath(effectiveRoot).Equals(paths.Root, StringComparison.OrdinalIgnoreCase))
        {
            paths = new GamePaths(effectiveRoot);
            paths.EnsureBaseDirectories();
            settingsStore = new SettingsStore(paths);
            settings = settingsStore.Load();
        }

        AppLog.Initialize(paths.LogsDir);

        var network = settings.ToNetworkOptions();
        var mirrors = new MirrorResolver(network);
        var http = HttpClients.Create(network);

        return new LauncherContext(paths, settingsStore, settings, http, network, mirrors);
    }

    /// <summary>Portable mode (a <c>portable.txt</c> beside the executable) stores data next to the EXE.</summary>
    public static string DefaultRootDirectory()
    {
        // 注意：单文件发布（尤其是 IncludeAllContentForSelfExtract）时，AppContext.BaseDirectory
        // 可能指向 **临时解包目录**（%TEMP%\.net\...），而不是 exe 所在的文件夹。
        // 用它去找 portable.txt 会让"便携模式"静默失效、数据仍然跑到 %APPDATA%。
        // Environment.ProcessPath 才是真正的 exe 路径，这里以它为准。
        var exeDirectory = ResolveExecutableDirectory();

        if (File.Exists(Path.Combine(exeDirectory, "portable.txt")))
            return Path.Combine(exeDirectory, "data");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) appData = exeDirectory;

        var current = Path.Combine(appData, "OrapCraftLauncher");

        // The launcher used to be called Orion; keep reading an existing data directory so a rename
        // never logs the user out or hides their versions. Nothing is moved or deleted.
        var legacy = Path.Combine(appData, "OrionLauncher");
        if (!Directory.Exists(current) && Directory.Exists(legacy)) return legacy;

        return current;
    }

    /// <summary>测试用：按指定的 exe 目录计算数据根目录（生产路径见 <see cref="DefaultRootDirectory"/>）。</summary>
    internal static string ResolveRootForTests(string exeDirectory)
    {
        if (File.Exists(Path.Combine(exeDirectory, "portable.txt")))
            return Path.Combine(exeDirectory, "data");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) appData = exeDirectory;

        var current = Path.Combine(appData, "OrapCraftLauncher");
        var legacy = Path.Combine(appData, "OrionLauncher");
        if (!Directory.Exists(current) && Directory.Exists(legacy)) return legacy;

        return current;
    }

    /// <summary>真正的 exe 所在目录（单文件发布下也不受临时解包目录影响）。</summary>
    private static string ResolveExecutableDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var directory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(directory)) return directory;
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>Registers version roots of imported instances so their versions can be launched.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsInitialized) return;

        var instances = Instances.LoadAll();
        var roots = Importer.VersionRootsOf(instances);

        foreach (var root in roots)
        {
            if (!VersionRepository.ExtraVersionRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                VersionRepository.ExtraVersionRoots.Add(root);
                AppLog.Info("context", "external-version-root", ("path", root));
            }
        }

        if (!string.IsNullOrWhiteSpace(Settings.JavaPath) && File.Exists(Settings.JavaPath))
        {
            AppLog.Info("context", "java-override", ("path", Settings.JavaPath));
        }

        IsInitialized = true;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a Microsoft client id at runtime (settings page / login panel) so the modern
    /// device-code and loopback flows start working without restarting the launcher.
    /// </summary>
    public void UpdateMicrosoftClientId(string? clientId)
    {
        var trimmed = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();

        Settings.MicrosoftClientId = trimmed;
        Auth.Options.ClientId = string.IsNullOrWhiteSpace(trimmed) ? new MicrosoftAuthOptions().ClientId : trimmed;

        AppLog.Info("auth", "client-id-updated", ("modern", Auth.Options.HasModernClientId));
    }

    /// <summary>The account that should be used for the next launch.</summary>
    public async Task<Account> GetActiveAccountAsync(CancellationToken ct = default)
    {
        var accounts = await AccountStore.LoadAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(Settings.SelectedAccountId))
        {
            var match = accounts.FirstOrDefault(a => string.Equals(a.Id, Settings.SelectedAccountId, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return accounts.FirstOrDefault() ?? OfflineAccounts.Create("Player");
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
