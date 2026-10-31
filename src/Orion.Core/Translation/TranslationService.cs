using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Orion.Core.Net;

namespace Orion.Core.Translation;

/// <summary>
/// 机器翻译（英文 → 简体中文）。
///
/// 用的是 MyMemory 的公开翻译记忆库接口：不需要 API Key，属于"翻译记忆 + 统计机器翻译"，
/// **不是**大语言模型，也没有本地模型。代价要说清楚：待翻译文本会发送到第三方服务，
/// 匿名额度有限（每天约 5000 字符），长描述会被切成多段逐段翻译。
///
/// 网络不通 / 超额度时如实返回失败原因，不会编造译文。
/// </summary>
public sealed class TranslationService(HttpClient http)
{
    private const string Endpoint = "https://api.mymemory.translated.net/get";

    /// <summary>单段上限：MyMemory 对 q 参数有长度限制，切小一点更稳。</summary>
    private const int ChunkSize = 450;

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <summary>上一次失败的原因（供界面显示）。</summary>
    public string? LastError { get; private set; }

    /// <summary>把英文文本翻成简体中文；失败返回 null。</summary>
    public async Task<string?> TranslateAsync(string? text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var normalized = text.Trim();
        if (_cache.TryGetValue(normalized, out var cached)) return cached;

        var chunks = Split(normalized);
        var translated = new List<string>(chunks.Count);

        foreach (var chunk in chunks)
        {
            var piece = await TranslateChunkAsync(chunk, ct).ConfigureAwait(false);
            if (piece is null) return null;
            translated.Add(piece);
        }

        var result = string.Join("\n\n", translated);
        _cache[normalized] = result;
        return result;
    }

    private async Task<string?> TranslateChunkAsync(string chunk, CancellationToken ct)
    {
        try
        {
            var url = $"{Endpoint}?q={Uri.EscapeDataString(chunk)}&langpair={Uri.EscapeDataString("en|zh-CN")}";
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                LastError = "翻译服务限流了（匿名额度每天约 5000 字符），稍后再试。";
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                LastError = $"翻译服务返回 HTTP {(int)response.StatusCode}。";
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("responseData", out var data) ||
                !data.TryGetProperty("translatedText", out var text))
            {
                LastError = "翻译服务返回的结构不认识。";
                return null;
            }

            var translated = text.GetString();
            if (string.IsNullOrWhiteSpace(translated))
            {
                LastError = "翻译服务返回了空结果。";
                return null;
            }

            // MyMemory 会在配额用尽时把错误信息塞进 translatedText
            if (translated.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
            {
                LastError = "翻译服务的匿名额度用完了，明天再试或换网络。";
                return null;
            }

            LastError = null;
            return translated;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LastError = "翻译请求失败：" + ex.Message;
            return null;
        }
    }

    /// <summary>按句子/长度切段，避免截断单词。</summary>
    internal static List<string> Split(string text)
    {
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                if (current.Length > 0) Flush();
                continue;
            }

            if (current.Length + line.Length + 1 > ChunkSize)
            {
                if (current.Length > 0) Flush();

                // 单行本身就超长：按句子/空格再切
                if (line.Length > ChunkSize)
                {
                    foreach (var piece in SplitLongLine(line))
                    {
                        if (current.Length + piece.Length + 1 > ChunkSize && current.Length > 0) Flush();
                        current.Append(piece).Append(' ');
                    }
                }
                else
                {
                    current.Append(line).Append(' ');
                }
            }
            else
            {
                current.Append(line).Append(' ');
            }
        }

        if (current.Length > 0) Flush();
        return chunks.Count == 0 ? [text] : chunks;

        void Flush()
        {
            var value = current.ToString().Trim();
            if (value.Length > 0) chunks.Add(value);
            current.Clear();
        }
    }

    private static IEnumerable<string> SplitLongLine(string line)
    {
        var words = line.Split(' ');
        var buffer = new System.Text.StringBuilder();

        foreach (var word in words)
        {
            if (buffer.Length + word.Length + 1 > ChunkSize && buffer.Length > 0)
            {
                yield return buffer.ToString().Trim();
                buffer.Clear();
            }

            buffer.Append(word).Append(' ');
        }

        if (buffer.Length > 0) yield return buffer.ToString().Trim();
    }
}
