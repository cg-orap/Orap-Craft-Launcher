using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orion.Core.Json;

#region Version manifest (launchermeta.mojang.com/mc/game/version_manifest_v2.json)

public sealed class VersionManifest
{
    [JsonPropertyName("latest")] public LatestVersions? Latest { get; set; }
    [JsonPropertyName("versions")] public List<ManifestVersion> Versions { get; set; } = [];
}

public sealed class LatestVersions
{
    [JsonPropertyName("release")] public string? Release { get; set; }
    [JsonPropertyName("snapshot")] public string? Snapshot { get; set; }
}

public sealed class ManifestVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "release";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("time")] public DateTimeOffset? Time { get; set; }
    [JsonPropertyName("releaseTime")] public DateTimeOffset? ReleaseTime { get; set; }
    [JsonPropertyName("complianceLevel")] public int ComplianceLevel { get; set; }
}

#endregion

#region Version metadata (the per-version JSON document)

public sealed class VersionMetadata
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("mainClass")] public string? MainClass { get; set; }

    /// <summary>
    /// Legacy field naming the version whose client JAR should be used ("jar": "1.12.2").
    /// Old Forge and OptiFine profiles rely on it.
    /// </summary>
    [JsonPropertyName("jar")] public string? Jar { get; set; }
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
    [JsonPropertyName("assetIndex")] public AssetIndexReference? AssetIndex { get; set; }
    [JsonPropertyName("assets")] public string? Assets { get; set; }
    [JsonPropertyName("downloads")] public VersionDownloads? Downloads { get; set; }
    [JsonPropertyName("javaVersion")] public JavaVersionRequirement? JavaVersion { get; set; }
    [JsonPropertyName("libraries")] public List<LibraryEntry>? Libraries { get; set; }
    [JsonPropertyName("arguments")] public LaunchArguments? Arguments { get; set; }
    [JsonPropertyName("minecraftArguments")] public string? MinecraftArguments { get; set; }
    [JsonPropertyName("logging")] public LoggingSettings? Logging { get; set; }
    [JsonPropertyName("minimumLauncherVersion")] public int MinimumLauncherVersion { get; set; }
    [JsonPropertyName("complianceLevel")] public int ComplianceLevel { get; set; }
    [JsonPropertyName("releaseTime")] public DateTimeOffset? ReleaseTime { get; set; }
    [JsonPropertyName("time")] public DateTimeOffset? Time { get; set; }
}

public sealed class AssetIndexReference
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("totalSize")] public long TotalSize { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class VersionDownloads
{
    [JsonPropertyName("client")] public DownloadInfo? Client { get; set; }
    [JsonPropertyName("server")] public DownloadInfo? Server { get; set; }
    [JsonPropertyName("client_mappings")] public DownloadInfo? ClientMappings { get; set; }
    [JsonPropertyName("server_mappings")] public DownloadInfo? ServerMappings { get; set; }
}

public sealed class DownloadInfo
{
    /// <summary>Repository relative path, e.g. <c>org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar</c>.</summary>
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class JavaVersionRequirement
{
    [JsonPropertyName("component")] public string? Component { get; set; }
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; }
}

public sealed class LoggingSettings
{
    [JsonPropertyName("client")] public LoggingClient? Client { get; set; }
}

public sealed class LoggingClient
{
    [JsonPropertyName("argument")] public string? Argument { get; set; }
    [JsonPropertyName("file")] public LoggingFile? File { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}

public sealed class LoggingFile
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class LaunchArguments
{
    [JsonPropertyName("game")] public List<ArgumentNode>? Game { get; set; }
    [JsonPropertyName("jvm")] public List<ArgumentNode>? Jvm { get; set; }
}

/// <summary>
/// An entry of the <c>arguments.game</c> / <c>arguments.jvm</c> arrays. The official
/// format allows either a bare string or an object carrying <c>rules</c> and a
/// <c>value</c> that is itself either a string or an array of strings.
/// </summary>
public sealed class ArgumentNode
{
    public List<Rule>? Rules { get; set; }
    public List<string> Values { get; set; } = [];

    public static ArgumentNode FromString(string value) => new() { Values = [value] };
}

public sealed class Rule
{
    [JsonPropertyName("action")] public string Action { get; set; } = "allow";
    [JsonPropertyName("os")] public OsRule? Os { get; set; }
    [JsonPropertyName("features")] public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsRule
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }
}

public sealed class LibraryEntry
{
    /// <summary>Maven coordinate, e.g. <c>org.lwjgl:lwjgl:3.3.3</c> or <c>group:artifact:version:classifier</c>.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; set; }

    /// <summary>Maps an OS name ("windows") to a classifier key ("natives-windows").</summary>
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }

    [JsonPropertyName("rules")] public List<Rule>? Rules { get; set; }

    [JsonPropertyName("extract")] public ExtractRules? Extract { get; set; }

    /// <summary>Maven repository base URL, used by Fabric/Quilt/Forge style metadata.</summary>
    [JsonPropertyName("url")] public string? Url { get; set; }

    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }

    public MavenCoordinate? Coordinate => MavenCoordinate.TryParse(Name, out var c) ? c : null;
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")] public DownloadInfo? Artifact { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, DownloadInfo>? Classifiers { get; set; }
}

public sealed class ExtractRules
{
    [JsonPropertyName("exclude")] public List<string>? Exclude { get; set; }
}

#endregion

#region Asset index

public sealed class AssetIndex
{
    [JsonPropertyName("objects")] public Dictionary<string, AssetObject> Objects { get; set; } = [];
    [JsonPropertyName("virtual")] public bool Virtual { get; set; }
    [JsonPropertyName("map_to_resources")] public bool MapToResources { get; set; }
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>Path relative to the asset root, i.e. <c>ab/abcdef....</c></summary>
    public string StoragePath => Hash.Length < 2 ? Hash : $"{Hash[..2]}/{Hash}";
}

#endregion

/// <summary>A parsed Maven coordinate (<c>group:artifact:version[:classifier][@ext]</c>).</summary>
public readonly record struct MavenCoordinate(string Group, string Artifact, string Version, string? Classifier, string Extension)
{
    public static bool TryParse(string? name, out MavenCoordinate coordinate)
    {
        coordinate = default;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var extension = "jar";
        var at = name.LastIndexOf('@');
        if (at >= 0)
        {
            extension = name[(at + 1)..];
            name = name[..at];
        }

        var parts = name.Split(':');
        if (parts.Length < 3) return false;

        var classifier = parts.Length >= 4 && parts[3].Length > 0 ? parts[3] : null;
        coordinate = new MavenCoordinate(parts[0], parts[1], parts[2], classifier, extension);
        return parts[0].Length > 0 && parts[1].Length > 0 && parts[2].Length > 0;
    }

    /// <summary>Repository relative path, e.g. <c>org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar</c>.</summary>
    public string ToRepositoryPath()
    {
        var folder = $"{Group.Replace('.', '/')}/{Artifact}/{Version}";
        var file = Classifier is null
            ? $"{Artifact}-{Version}.{Extension}"
            : $"{Artifact}-{Version}-{Classifier}.{Extension}";
        return $"{folder}/{file}";
    }

    /// <summary>Only group and artifact: two entries with the same key are the same library.</summary>
    public string GroupArtifact => $"{Group}:{Artifact}";

    public MavenCoordinate WithClassifier(string? classifier) => this with { Classifier = classifier };

    public override string ToString()
        => Classifier is null ? $"{Group}:{Artifact}:{Version}" : $"{Group}:{Artifact}:{Version}:{Classifier}";
}

/// <summary>
/// Reads <see cref="ArgumentNode"/> values that may be a bare string or an object with
/// <c>rules</c> and a string-or-array <c>value</c>.
/// </summary>
public sealed class ArgumentNodeConverter : JsonConverter<ArgumentNode>
{
    public override ArgumentNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return ArgumentNode.FromString(reader.GetString() ?? "");

            case JsonTokenType.StartObject:
            {
                var node = new ArgumentNode();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) return node;
                    if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a property name in an argument node.");

                    var property = reader.GetString();
                    reader.Read();
                    switch (property)
                    {
                        case "rules":
                            node.Rules = JsonSerializer.Deserialize<List<Rule>>(ref reader, options);
                            break;

                        case "value":
                            if (reader.TokenType == JsonTokenType.String)
                            {
                                node.Values.Add(reader.GetString() ?? "");
                            }
                            else if (reader.TokenType == JsonTokenType.StartArray)
                            {
                                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                                {
                                    switch (reader.TokenType)
                                    {
                                        case JsonTokenType.String:
                                            node.Values.Add(reader.GetString() ?? "");
                                            break;
                                        case JsonTokenType.Number:
                                            node.Values.Add(reader.TryGetInt64(out var number)
                                                ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture));
                                            break;
                                        case JsonTokenType.True:
                                            node.Values.Add("true");
                                            break;
                                        case JsonTokenType.False:
                                            node.Values.Add("false");
                                            break;
                                        default:
                                            reader.Skip();
                                            break;
                                    }
                                }
                            }
                            else reader.Skip();
                            break;

                        default:
                            reader.Skip();
                            break;
                    }
                }

                throw new JsonException("Unterminated argument node object.");
            }

            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for an argument node.");
        }
    }

    public override void Write(Utf8JsonWriter writer, ArgumentNode value, JsonSerializerOptions options)
    {
        if (value.Rules is null or { Count: 0 })
        {
            if (value.Values.Count == 1)
            {
                writer.WriteStringValue(value.Values[0]);
                return;
            }

            writer.WriteStartObject();
            writer.WritePropertyName("value");
            writer.WriteStartArray();
            foreach (var item in value.Values) writer.WriteStringValue(item);
            writer.WriteEndArray();
            writer.WriteEndObject();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("rules");
        JsonSerializer.Serialize(writer, value.Rules, options);
        writer.WritePropertyName("value");
        if (value.Values.Count == 1)
        {
            writer.WriteStringValue(value.Values[0]);
        }
        else
        {
            writer.WriteStartArray();
            foreach (var item in value.Values) writer.WriteStringValue(item);
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}
