using System.Text.Json;
using System.Text.Json.Serialization;
using Orion.Core.Json;

namespace Orion.Core.Util;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> instances.
/// The launcher metadata format is camelCase, so a camelCase naming policy lets the
/// model classes use normal PascalCase property names. Fields whose wire name is
/// <em>not</em> camelCase (for example <c>client_mappings</c>) are decorated with
/// <see cref="JsonPropertyNameAttribute"/>.
/// </summary>
public static class JsonUtil
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Compact { get; } = new(Options)
    {
        WriteIndented = false,
    };

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        options.Converters.Add(new ArgumentNodeConverter());
        options.Converters.Add(new FlexibleStringConverter());
        return options;
    }

    public static string Serialize<T>(T value, bool indented = true)
        => JsonSerializer.Serialize(value, indented ? Options : Compact);

    public static T? Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options);

    public static async Task<T?> DeserializeFileAsync<T>(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct).ConfigureAwait(false);
    }

    public static async Task WriteFileAsync<T>(string path, T value, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Write to a temporary file first and move it into place so that a crash or a
        // cancellation can never leave a half-written JSON file behind.
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, value, Options, ct).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Some launcher metadata (notably mod loader metadata) encodes numbers as strings and
/// booleans as <c>0</c>/<c>1</c>. This converter keeps string properties tolerant.
/// </summary>
internal sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected token {reader.TokenType} for a string property."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
