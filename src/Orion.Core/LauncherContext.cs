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
        PackArchives = new Mods.PackArchiveInstaller(paths, Instances, Loaders, Importer, http, settings.CurseForgeApiKey);
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
        var baseDirectory = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDirectory, "portable.txt")))
            return Path.Combine(baseDirectory, "data");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) appData = baseDirectory;

        var current = Path.Combine(appData, "OrapCraftLauncher");

        // The launcher used to be called Orion; keep reading an existing data directory so a rename
        // never logs the user out or hides their versions. Nothing is moved or deleted.
        var legacy = Path.Combine(appData, "OrionLauncher");
        if (!Directory.Exists(current) && Directory.Exists(legacy)) return legacy;

        return current;
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
