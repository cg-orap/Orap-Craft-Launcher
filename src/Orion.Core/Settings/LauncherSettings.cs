using System.Text.Json.Serialization;
using Orion.Core.Net;
using Orion.Core.Util;

namespace Orion.Core.Settings;

/// <summary>Launcher wide settings, persisted as JSON under the data root.</summary>
public sealed class LauncherSettings
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    /// <summary>Custom data root; empty means the default (%APPDATA%\OrionLauncher).</summary>
    [JsonPropertyName("rootDirectory")] public string? RootDirectory { get; set; }

    [JsonPropertyName("javaPath")] public string? JavaPath { get; set; }
    [JsonPropertyName("memoryMb")] public int MemoryMb { get; set; } = 4096;
    [JsonPropertyName("minMemoryMb")] public int MinMemoryMb { get; set; } = 512;
    [JsonPropertyName("jvmArguments")] public string? JvmArguments { get; set; }

        /// <summary>
    /// Files downloaded at the same time. Minecraft assets are thousands of small objects, so the
    /// bottleneck is round-trip latency rather than bandwidth: a higher fan-out is what lifts
    /// throughput from tens of KB/s into the MB/s range.
    /// </summary>
    [JsonPropertyName("maxConcurrency")] public int MaxConcurrency { get; set; } = 16;
    [JsonPropertyName("source")] public DownloadSource Source { get; set; } = DownloadSource.Official;
    [JsonPropertyName("customMirrorBase")] public string? CustomMirrorBase { get; set; }
    [JsonPropertyName("proxyUrl")] public string? ProxyUrl { get; set; }

    [JsonPropertyName("theme")] public string Theme { get; set; } = "Rock";

    /// <summary>Accent colour scheme: Blue / Violet / Teal / Amber / Rose.</summary>
    [JsonPropertyName("accent")] public string Accent { get; set; } = "Moss";
    [JsonPropertyName("language")] public string Language { get; set; } = "zh-CN";
    [JsonPropertyName("closeLauncherOnGameStart")] public bool CloseLauncherOnGameStart { get; set; }
    [JsonPropertyName("forceUtf8Console")] public bool ForceUtf8Console { get; set; } = true;

    [JsonPropertyName("microsoftClientId")] public string? MicrosoftClientId { get; set; }
    [JsonPropertyName("selectedAccountId")] public string? SelectedAccountId { get; set; }
    [JsonPropertyName("lastInstanceId")] public string? LastInstanceId { get; set; }

    /// <summary>Custom home page background image; empty shows the built-in gradient.</summary>
    [JsonPropertyName("backgroundImagePath")] public string? BackgroundImagePath { get; set; }


        /// <summary>
    /// Optional CurseForge API key. Without one, CurseForge packs can only be resolved through public
    /// CDN guesses, which misses many files; with a key the official download-url endpoint is used.
    ///
    /// 这个属性只存在内存里：落盘时由 <see cref="SettingsStore"/> 换成 DPAPI 密文写进
    /// <see cref="CurseForgeApiKeyProtected"/>，明文不会再出现在 settings.json。
    /// </summary>
    [JsonIgnore] public string? CurseForgeApiKey { get; set; }

    /// <summary>CurseForge API Key 的 DPAPI 密文（Base64，当前 Windows 用户才能解开）。</summary>
    [JsonPropertyName("curseForgeKeyProtected")] public string? CurseForgeApiKeyProtected { get; set; }

    /// <summary>旧版本留下的明文字段：读取后会被迁移成密文并在下次保存时清空。</summary>
    [JsonPropertyName("curseForgeApiKey")] public string? LegacyCurseForgeApiKey { get; set; }

    /// <summary>工具箱 → 游戏默认设置：新版本初始化时写入 options.txt。</summary>
    [JsonPropertyName("defaultGameOptions")] public Game.GameOptionsTemplate DefaultGameOptions { get; set; } = new();

    /// <summary>备份配额（MB）：侧边栏「备份占用」的分母，超过 90% 会变色提醒。</summary>
    [JsonPropertyName("backupQuotaMb")] public int BackupQuotaMb { get; set; } = 8192;

    [JsonPropertyName("showSnapshots")] public bool ShowSnapshots { get; set; } = true;
    [JsonPropertyName("showOldVersions")] public bool ShowOldVersions { get; set; } = true;

    public NetworkOptions ToNetworkOptions() => new()
    {
        Source = Source,
        CustomMirrorBase = CustomMirrorBase,
        ProxyUrl = ProxyUrl,
        MaxConnectionsPerServer = Math.Clamp(MaxConcurrency * 2, 64, 512),
    };

    public LauncherSettings Clone() => (LauncherSettings)MemberwiseClone();
}

public sealed class SettingsStore
{
    private readonly Game.GamePaths _paths;

    public SettingsStore(Game.GamePaths paths) => _paths = paths;

    public LauncherSettings Current { get; private set; } = new();

    public LauncherSettings Load()
    {
        try
        {
            if (File.Exists(_paths.SettingsFile))
            {
                var json = File.ReadAllText(_paths.SettingsFile);
                var settings = JsonUtil.Deserialize<LauncherSettings>(json);
                if (settings is not null)
                {
                    // 老配置里的明文 Key 迁移到内存，下次保存会写成密文并抹掉明文
                    settings.CurseForgeApiKey = string.IsNullOrWhiteSpace(settings.LegacyCurseForgeApiKey)
                        ? Util.SecretProtector.Unprotect(settings.CurseForgeApiKeyProtected)
                        : settings.LegacyCurseForgeApiKey;

                    if (!string.IsNullOrWhiteSpace(settings.LegacyCurseForgeApiKey))
                    {
                        // 迁移绝不能拖垮启动：任何异常都只记一条日志，内存里的 Key 仍然可用
                        try
                        {
                            Diagnostics.AppLog.Info("settings", "migrating plaintext CurseForge key to DPAPI");
                            var protectedValue = Util.SecretProtector.Protect(settings.CurseForgeApiKey);
                            if (protectedValue is not null)
                            {
                                settings.CurseForgeApiKeyProtected = protectedValue;
                                settings.LegacyCurseForgeApiKey = null;

                                // 立刻落盘：不让明文在磁盘上多留一次运行。
                                // 这里必须用同步写：Load() 在 WPF 的 UI 线程上被调用，await 一个
                                // 捕获了 Dispatcher 上下文的异步写会直接死锁（窗口永不出现）。
                                File.WriteAllText(_paths.SettingsFile, JsonUtil.Serialize(settings));
                            }
                        }
                        catch (Exception migrateError)
                        {
                            Diagnostics.AppLog.Warn("settings", "migrate-failed", ("error", migrateError.Message));
                        }
                    }

                    Current = settings;
                }
            }
        }
        catch (Exception ex)
        {
            // 配置读失败不该阻止启动：用默认值继续，并把原因写进日志
            Diagnostics.AppLog.Warn("settings", "unreadable", ("error", ex.Message));
        }

        return Current;
    }

    public async Task SaveAsync(LauncherSettings? settings = null, CancellationToken ct = default)
    {
        if (settings is not null) Current = settings;

        // 明文只写进 DPAPI 密文；LegacyCurseForgeApiKey 一律清空，避免旧字段继续泄露
        if (!string.IsNullOrWhiteSpace(Current.CurseForgeApiKey))
        {
            var protectedValue = Util.SecretProtector.Protect(Current.CurseForgeApiKey);
            if (protectedValue is not null) Current.CurseForgeApiKeyProtected = protectedValue;
        }

        Current.LegacyCurseForgeApiKey = null;

        await JsonUtil.WriteFileAsync(_paths.SettingsFile, Current, ct).ConfigureAwait(false);
    }
}
