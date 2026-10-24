using System.Net;
using System.Text.Json;
using Orion.Core.Util;

namespace Orion.Core.Net;

/// <summary>Small JSON/text GET helper with mirror fallback and bounded retries.</summary>
public static class HttpUtil
{
    public static async Task<string> GetStringAsync(
        HttpClient http,
        IReadOnlyList<Uri> urls,
        CancellationToken ct = default,
        int attemptsPerUrl = 2)
    {
        if (urls.Count == 0) throw new ArgumentException("At least one URL is required.", nameof(urls));

        Exception? last = null;
        foreach (var url in urls)
        {
            for (var attempt = 0; attempt < attemptsPerUrl; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        last = new HttpStatusException(response.StatusCode, response.Headers.RetryAfter?.Delta);
                        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                            break; // no point retrying this URL
                    }
                    else
                    {
                        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    last = ex;
                }

                if (attempt + 1 < attemptsPerUrl)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), ct).ConfigureAwait(false);
                }
            }
        }

        throw new HttpRequestException($"请求失败: {urls[0]} (已尝试 {urls.Count} 个来源)", last);
    }

    public static async Task<T> GetJsonAsync<T>(
        HttpClient http,
        IReadOnlyList<Uri> urls,
        CancellationToken ct = default,
        int attemptsPerUrl = 2)
    {
        var text = await GetStringAsync(http, urls, ct, attemptsPerUrl).ConfigureAwait(false);

        try
        {
            var value = JsonSerializer.Deserialize<T>(text, JsonUtil.Options);
            return value ?? throw new JsonException("JSON 根元素为 null。");
        }
        catch (JsonException ex)
        {
            throw new JsonException($"无法解析来自 {urls[0]} 的 JSON 响应: {ex.Message}", ex);
        }
    }
}
