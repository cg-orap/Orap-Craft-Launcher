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
    [JsonPropertyName("maxConcurrency")] public int MaxConcurrency { get; set; } = 32;
    [JsonPropertyName("source")] public DownloadSource Source { get; set; } = DownloadSource.Official;
    [JsonPropertyName("customMirrorBase")] public string? CustomMirrorBase { get; set; }
    [JsonPropertyName("proxyUrl")] public string? ProxyUrl { get; set; }

    [JsonPropertyName("theme")] public string Theme { get; set; } = "Midnight";

    /// <summary>Accent colour scheme: Blue / Violet / Teal / Amber / Rose.</summary>
    [JsonPropertyName("accent")] public string Accent { get; set; } = "Blue";
    [JsonPropertyName("language")] public string Language { get; set; } = "zh-CN";
    [JsonPropertyName("closeLauncherOnGameStart")] public bool CloseLauncherOnGameStart { get; set; }
    [JsonPropertyName("forceUtf8Console")] public bool ForceUtf8Console { get; set; } = true;

    [JsonPropertyName("microsoftClientId")] public string? MicrosoftClientId { get; set; }
    [JsonPropertyName("selectedAccountId")] public string? SelectedAccountId { get; set; }
    [JsonPropertyName("lastInstanceId")] public string? LastInstanceId { get; set; }

    /// <summary>Custom home page background image; empty shows the built-in gradient.</summary>
    [JsonPropertyName("backgroundImagePath")] public string? BackgroundImagePath { get; set; }


    [JsonPropertyName("showSnapshots")] public bool ShowSnapshots { get; set; } = true;
    [JsonPropertyName("showOldVersions")] public bool ShowOldVersions { get; set; } = true;

    public NetworkOptions ToNetworkOptions() => new()
    {
        Source = Source,
        CustomMirrorBase = CustomMirrorBase,
        ProxyUrl = ProxyUrl,
        MaxConnectionsPerServer = 64,
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
                if (settings is not null) Current = settings;
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            Diagnostics.AppLog.Warn("settings", "unreadable", ("error", ex.Message));
        }

        return Current;
    }

    public async Task SaveAsync(LauncherSettings? settings = null, CancellationToken ct = default)
    {
        if (settings is not null) Current = settings;
        await JsonUtil.WriteFileAsync(_paths.SettingsFile, Current, ct).ConfigureAwait(false);
    }
}
