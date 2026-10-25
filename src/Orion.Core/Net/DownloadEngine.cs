using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Orion.Core.Diagnostics;
using Orion.Core.Util;

namespace Orion.Core.Net;

public enum DownloadVerifyPolicy
{
    /// <summary>Only hash when the file size is unknown or does not match. Fastest.</summary>
    Auto = 0,

    /// <summary>Always hash an existing file before reusing it.</summary>
    Always = 1,
}

public sealed record DownloadRequest
{
    public required string TargetPath { get; init; }

    /// <summary>Candidate URLs, most preferred first (mirror first when a mirror is enabled).</summary>
    public required IReadOnlyList<Uri> Urls { get; init; }

    public string? Sha1 { get; init; }
    public string? Sha256 { get; init; }

    /// <summary>Expected size in bytes, or 0 when the metadata does not declare one.</summary>
    public long Size { get; init; }

    public string? Label { get; init; }

    public bool AllowResume { get; init; } = true;

    public DownloadVerifyPolicy Verify { get; init; } = DownloadVerifyPolicy.Auto;

    public string DisplayName => Label ?? Path.GetFileName(TargetPath);
}

public sealed class DownloadEngineOptions
{
    public int MaxConcurrency { get; set; } = 32;
    public int MaxRetries { get; set; } = 4;
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(200);
    public string? FailureReportDirectory { get; set; }
}

public sealed record DownloadFailure(string TargetPath, string? Url, int? StatusCode, string Message, string ExceptionType);

public sealed record DownloadProgress(
    int TotalFiles,
    int CompletedFiles,
    long TotalBytes,
    long DownloadedBytes,
    double BytesPerSecond,
    TimeSpan Elapsed,
    string? CurrentFile,
    int FailedFiles)
{
    public double Fraction => TotalBytes > 0
        ? Math.Clamp(DownloadedBytes / (double)TotalBytes, 0, 1)
        : TotalFiles > 0 ? Math.Clamp(CompletedFiles / (double)TotalFiles, 0, 1) : 0;
}

public sealed class DownloadReport
{
    public int TotalFiles { get; init; }
    public int CompletedFiles { get; init; }
    public int SkippedFiles { get; init; }
    public int FailedFiles { get; init; }
    public long DownloadedBytes { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<DownloadFailure> Failures { get; init; } = [];
    public bool Success => FailedFiles == 0;
    public bool Cancelled { get; init; }
}

/// <summary>
/// Bounded-concurrency file downloader with HTTP range resume, exponential backoff,
/// digest verification, atomic commit and per/total progress reporting.
///
/// Behavioural contract:
/// * Data is written to <c>&lt;target&gt;.part</c> and only moved into place after the
///   declared SHA-1/SHA-256 digest matches, so a half-downloaded file is never mistaken
///   for a valid one.
/// * Every request is bounded by <see cref="DownloadEngineOptions.MaxConcurrency"/>.
/// * Duplicate target paths inside one batch are merged, so the same file can never be
///   downloaded twice concurrently.
/// * Cancellation leaves <c>.part</c> files behind on purpose: the next run resumes them.
/// * Servers that ignore <c>Range</c> (200 instead of 206) are detected and handled by
///   restarting that single file, never by corrupting it.
/// </summary>
public sealed class DownloadEngine
{
    private const int BufferSize = 96 * 1024;

    /// <summary>Files at least this large are fetched with several parallel range requests.</summary>
    private const long LargeFileThreshold = 4L * 1024 * 1024;

    /// <summary>Upper bound on parallel connections for one large file.</summary>
    private const int MaxChunksPerFile = 6;

    /// <summary>No-data budget for a single body read; see ReadWithStallGuardAsync.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(25);

    private readonly HttpClient _http;
    private readonly DownloadEngineOptions _options;
    private readonly MirrorResolver _mirrors;

    /// <summary>
    /// Hosts that recently failed at the transport level, mapped to the moment they may be
    /// tried first again. Without this, a single dead CDN host costs a connect timeout per
    /// file, which turns a 3000 file install into a very long wait.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _hostPenalty = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan HostPenaltyDuration = TimeSpan.FromMinutes(5);

    public DownloadEngine(HttpClient http, DownloadEngineOptions options, MirrorResolver mirrors)
    {
        _http = http;
        _options = options;
        _mirrors = mirrors;
    }

    public DownloadEngineOptions Options => _options;

    public async Task<DownloadReport> DownloadAsync(
        IReadOnlyList<DownloadRequest> requests,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var state = new RunState(progress);
        var started = DateTimeOffset.UtcNow;

        // 1. De-duplicate by target path (Windows paths are case insensitive).
        var unique = new Dictionary<string, DownloadRequest>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            var key = Path.GetFullPath(request.TargetPath);
            if (unique.TryGetValue(key, out var existing))
            {
                unique[key] = Merge(existing, request);
            }
            else
            {
                unique[key] = request with { TargetPath = key };
            }
        }

        var work = unique.Values.ToList();
        state.TotalFiles = work.Count;
        state.TotalBytes = work.Sum(r => Math.Max(0, r.Size));

        AppLog.Debug("download", "batch-start", ("files", work.Count), ("bytes", state.TotalBytes), ("concurrency", _options.MaxConcurrency));

        // 2. Skip files that are already on disk and intact.
        var pending = new List<DownloadRequest>(work.Count);
        foreach (var request in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsUpToDateAsync(request, state, cancellationToken).ConfigureAwait(false))
            {
                state.OnSkipped(request.Size);
                continue;
            }

            pending.Add(request);
        }

        if (pending.Count == 0)
        {
            return Finish(state, started, cancelled: false);
        }

        state.StartReporterIfNeeded();

        // 3. Bounded concurrent transfer.
        //
        // Thousands of tiny asset objects are latency bound, not bandwidth bound: at the user's
        // concurrency of 8 they crawled along at ~22 KB/s. A batch that is clearly made of small
        // files therefore gets a wider fan-out automatically (still bounded, still per-host safe).
        var fanout = Math.Max(1, _options.MaxConcurrency);
        var smallBatch = pending.Count >= 200 && pending.All(p => p.Size is > 0 and < 1024 * 1024);
        if (smallBatch) fanout = Math.Min(64, fanout * 3);
        if (smallBatch) AppLog.Debug("download", "small-batch-fanout", ("files", pending.Count), ("fanout", fanout));

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = fanout,
            CancellationToken = cancellationToken,
        };

        try
        {
            await Parallel.ForEachAsync(pending, parallelOptions, async (request, ct) =>
            {
                state.SetCurrentFile(request.DisplayName);
                var result = await DownloadOneAsync(request, state, ct).ConfigureAwait(false);
                if (result is null)
                {
                    state.OnCompleted(request.Size);
                }
                else
                {
                    state.Failures.Add(result);
                    AppLog.Warn("download", "file-failed",
                        ("target", request.TargetPath),
                        ("url", result.Url),
                        ("status", result.StatusCode),
                        ("error", result.Message));
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.StopReporter();
            AppLog.Info("download", "batch-cancelled", ("completed", state.CompletedFilesView), ("total", work.Count));
            return Finish(state, started, cancelled: true);
        }

        state.StopReporter();
        state.ReportFinal();

        var report = Finish(state, started, cancelled: false);
        await WriteFailureReportAsync(report).ConfigureAwait(false);
        AppLog.Info("download", "batch-finished",
            ("ok", report.CompletedFiles),
            ("skipped", report.SkippedFiles),
            ("failed", report.FailedFiles),
            ("bytes", report.DownloadedBytes),
            ("seconds", Math.Round(report.Duration.TotalSeconds, 1)));

        return report;
    }

    /// <summary>Returns null on success, or a failure record describing the last error.</summary>
    private async Task<DownloadFailure?> DownloadOneAsync(DownloadRequest request, RunState state, CancellationToken ct)
    {
        Exception? last = null;
        Uri? lastUrl = null;
        int? lastStatus = null;

        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var ordered = OrderByHostHealth(request.Urls);
            var url = ordered[Math.Min(attempt, ordered.Count - 1)];
            lastUrl = url;

            try
            {
                await TransferAsync(request, url, state, ct).ConfigureAwait(false);
                _hostPenalty.TryRemove(url.Host, out _);
                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpStatusException ex)
            {
                last = ex;
                lastStatus = (int)ex.StatusCode;
                if (ex.IsPermanent)
                {
                    AppLog.Warn("download", "permanent-status", ("url", url.ToString()), ("status", (int)ex.StatusCode));
                    break;
                }

                if (ex.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero)
                {
                    await DelayAsync(retryAfter, ct).ConfigureAwait(false);
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                last = ex;
                PenalizeHost(url, ex);
            }
            catch (TaskCanceledException ex)
            {
                // A per-request timeout (HttpClient.Timeout / ConnectTimeout) surfaces as a
                // TaskCanceledException even though the caller never asked to cancel. That is
                // a transport failure and must be retried, not reported as "user cancelled".
                last = ex;
                PenalizeHost(url, ex);
            }

            if (attempt < _options.MaxRetries)
            {
                await DelayAsync(Backoff(attempt), ct).ConfigureAwait(false);
            }
        }

        return new DownloadFailure(
            request.TargetPath,
            lastUrl?.ToString(),
            lastStatus,
            last?.Message ?? "unknown error",
            last?.GetType().Name ?? "Unknown");
    }

    /// <summary>
    /// Reads the response body, treating "no bytes for StallTimeout" as a transport failure.
    ///
    /// Without this a connection that opens and then goes silent held the whole install forever: the
    /// progress sat at 72/74 with 0 B/s because the read awaited a socket that never answered.
    /// Throwing TaskCanceledException hands control back to DownloadOneAsync, which retries the next
    /// mirror and finally reports the offending URL.
    /// </summary>
    /// <summary>
    /// Downloads one large file as several parallel range requests and concatenates them into the
    /// usual .part file. Returns false (leaving no partial state) when the host does not support
    /// ranges or a chunk fails, so the caller can fall back to a single stream.
    /// </summary>
    private async Task<bool> TryChunkedAsync(DownloadRequest request, Uri url, RunState state, CancellationToken ct)
    {
        var size = request.Size;
        var chunks = Math.Clamp(_options.MaxConcurrency / 2, 2, MaxChunksPerFile);
        var chunkSize = size / chunks;
        if (chunkSize < 256 * 1024) return false;

        var part = request.TargetPath + ".part";
        var temp = new string[chunks];

        try
        {
            using (var probe = new HttpRequestMessage(HttpMethod.Get, url))
            {
                probe.Headers.Range = new RangeHeaderValue(0, 0);
                using var probeResponse = await _http
                    .SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (probeResponse.StatusCode != HttpStatusCode.PartialContent) return false;
            }

            var directory = Path.GetDirectoryName(part);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var jobs = new List<Task>(chunks);

            for (var i = 0; i < chunks; i++)
            {
                var index = i;
                var start = (long)index * chunkSize;
                var end = index == chunks - 1 ? size - 1 : start + chunkSize - 1;
                temp[index] = part + "." + index;

                jobs.Add(Task.Run(async () =>
                {
                    using var message = new HttpRequestMessage(HttpMethod.Get, url);
                    message.Headers.Range = new RangeHeaderValue(start, end);

                    using var response = await _http
                        .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);

                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        throw new HttpStatusException(response.StatusCode, null);
                    }

                    var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try
                    {
                        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        await using var sink = new FileStream(temp[index], FileMode.Create, FileAccess.Write,
                            FileShare.None, BufferSize, useAsync: true);

                        int read;
                        while ((read = await ReadWithStallGuardAsync(source, buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            state.OnBytesTransferred(read);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }, ct));
            }

            await Task.WhenAll(jobs).ConfigureAwait(false);

            await using (var combined = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                foreach (var chunk in temp)
                {
                    await using var source = new FileStream(chunk, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
                    await source.CopyToAsync(combined, ct).ConfigureAwait(false);
                }
            }

            foreach (var chunk in temp) TryDelete(chunk);
            return new FileInfo(part).Length == size;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or HttpStatusException or TaskCanceledException)
        {
            AppLog.Debug("download", "chunked-fallback", ("target", request.TargetPath), ("error", ex.Message));
            for (var i = 0; i < chunks; i++) TryDelete(part + "." + i);
            TryDelete(part);
            return false;
        }
    }
    private async Task<int> ReadWithStallGuardAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);

        try
        {
            return await source.ReadAsync(buffer.AsMemory(0, BufferSize), stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLog.Warn("download", "stalled", ("seconds", StallTimeout.TotalSeconds));
            throw new TaskCanceledException($"下载停滞：{StallTimeout.TotalSeconds:0} 秒没有收到数据");
        }
    }
    private async Task TransferAsync(DownloadRequest request, Uri url, RunState state, CancellationToken ct)
    {
        var target = request.TargetPath;
        var part = target + ".part";
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var resumeFrom = request.AllowResume && File.Exists(part) ? new FileInfo(part).Length : 0;
        if (resumeFrom > 0 && request.Size > 0 && resumeFrom >= request.Size)
        {
            // A previous run already stored the full body; verify instead of re-downloading.
            if (await IsIntactAsync(part, request, ct).ConfigureAwait(false))
            {
                Commit(part, target);
                return;
            }

            File.Delete(part);
            resumeFrom = 0;
        }

        // One connection is often throttled to a few KB/s while several together are fast (the same
        // trick PCL2 uses for big files). Chunking only engages for large files on hosts that honour
        // range requests; any problem falls back to the single-stream path below.
        var chunked = false;
        if (request.Size >= LargeFileThreshold && resumeFrom == 0)
        {
            chunked = await TryChunkedAsync(request, url, state, ct).ConfigureAwait(false);
        }

        if (!chunked)
        {
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0)
        {
            message.Headers.Range = new RangeHeaderValue(resumeFrom, null);
            message.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"orion-resume\""));
        }

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The partial file is longer than what the server has; start over.
            TryDelete(part);
            throw new HttpStatusException(HttpStatusCode.RequestedRangeNotSatisfiable, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpStatusException(response.StatusCode, response.Headers.RetryAfter?.Delta);
        }

        var appending = response.StatusCode == HttpStatusCode.PartialContent && resumeFrom > 0;
        var offset = appending ? resumeFrom : 0;

        if (resumeFrom > 0 && !appending)
        {
            AppLog.Debug("download", "range-unsupported", ("url", url.ToString()), ("target", target));
        }

        var declaredLength = response.Content.Headers.ContentLength;
        if (request.Size > 0 && declaredLength is > 0 && offset + declaredLength.Value != request.Size && !appending)
        {
            // Content-Length disagreed with the metadata; keep going, the hash check decides.
            AppLog.Debug("download", "size-mismatch-metadata",
                ("target", target), ("expected", request.Size), ("actual", offset + declaredLength.Value));
        }

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var sink = new FileStream(
                part,
                appending ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                useAsync: true);

            int read;
            while ((read = await ReadWithStallGuardAsync(source, buffer, ct).ConfigureAwait(false)) > 0)
            {
                await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                state.OnBytesTransferred(read);
            }

            await sink.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        }

        var length = new FileInfo(part).Length;
        if (request.Size > 0 && length != request.Size)
        {
            TryDelete(part);
            throw new InvalidDataException($"下载大小不匹配: 期望 {request.Size} 字节, 实际 {length} 字节");
        }

        if (!await IsIntactAsync(part, request, ct).ConfigureAwait(false))
        {
            TryDelete(part);
            throw new InvalidDataException("文件校验失败 (SHA 摘要不匹配)");
        }

        Commit(part, target);
    }

    private static void Commit(string part, string target)
    {
        // Same volume => atomic replace; a reader can never observe a partial file.
        File.Move(part, target, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private async Task<bool> IsUpToDateAsync(DownloadRequest request, RunState state, CancellationToken ct)
    {
        var target = request.TargetPath;
        if (!File.Exists(target)) return false;

        long length;
        try
        {
            length = new FileInfo(target).Length;
        }
        catch (IOException)
        {
            return false;
        }

        if (request.Size > 0 && length != request.Size)
        {
            TryDelete(target);
            return false;
        }

        if (request.Verify == DownloadVerifyPolicy.Always || request.Size <= 0)
        {
            if (await IsIntactAsync(target, request, ct).ConfigureAwait(false)) return true;

            AppLog.Warn("download", "hash-mismatch-repair", ("target", target));
            TryDelete(target);
            return false;
        }

        return true;
    }

    private static async Task<bool> IsIntactAsync(string path, DownloadRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.Sha1))
            return await FileHashing.VerifyAsync(path, request.Sha1, HashAlgorithmKind.Sha1, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(request.Sha256))
            return await FileHashing.VerifyAsync(path, request.Sha256, HashAlgorithmKind.Sha256, ct).ConfigureAwait(false);

        return true;
    }

    private void PenalizeHost(Uri url, Exception failure)
    {
        // Only connection level problems indicate a broken host; a bad digest does not.
        if (failure is InvalidDataException or System.Security.Cryptography.CryptographicException) return;

        _hostPenalty[url.Host] = DateTimeOffset.UtcNow + HostPenaltyDuration;
    }

    private List<Uri> OrderByHostHealth(IReadOnlyList<Uri> urls)
    {
        if (urls.Count <= 1) return [.. urls];

        var now = DateTimeOffset.UtcNow;
        var healthy = new List<Uri>(urls.Count);
        var unhealthy = new List<Uri>(urls.Count);

        foreach (var url in urls)
        {
            if (_hostPenalty.TryGetValue(url.Host, out var until) && until > now) unhealthy.Add(url);
            else healthy.Add(url);
        }

        healthy.AddRange(unhealthy);
        return healthy;
    }

    private TimeSpan Backoff(int attempt)
    {
        var exponential = _options.BaseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt);
        var capped = Math.Min(exponential, _options.MaxRetryDelay.TotalMilliseconds);
        var jitter = Random.Shared.Next(0, 250);
        return TimeSpan.FromMilliseconds(capped + jitter);
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return;

        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private DownloadReport Finish(RunState state, DateTimeOffset started, bool cancelled)
    {
        var failures = state.Failures.ToArray();
        return new DownloadReport
        {
            TotalFiles = state.TotalFiles,
            CompletedFiles = state.CompletedFilesView,
            SkippedFiles = state.SkippedFiles,
            FailedFiles = failures.Length,
            DownloadedBytes = state.DownloadedBytes,
            Duration = DateTimeOffset.UtcNow - started,
            Failures = failures,
            Cancelled = cancelled,
        };
    }

    private async Task WriteFailureReportAsync(DownloadReport report)
    {
        if (_options.FailureReportDirectory is null || report.Failures.Count == 0) return;

        try
        {
            Directory.CreateDirectory(_options.FailureReportDirectory);
            var path = Path.Combine(_options.FailureReportDirectory, $"download-failures-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            await JsonUtil.WriteFileAsync(path, new
            {
                generatedAt = DateTimeOffset.Now,
                totalFiles = report.TotalFiles,
                failedFiles = report.FailedFiles,
                failures = report.Failures.Select(f => new
                {
                    f.TargetPath,
                    f.Url,
                    f.StatusCode,
                    f.Message,
                    f.ExceptionType,
                }),
            }).ConfigureAwait(false);
            AppLog.Warn("download", "failure-report-written", ("path", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("download", "failure-report-failed", ("error", ex.Message));
        }
    }

    private static DownloadRequest Merge(DownloadRequest a, DownloadRequest b)
    {
        var urls = new List<Uri>(a.Urls);
        foreach (var uri in b.Urls)
        {
            if (!urls.Contains(uri)) urls.Add(uri);
        }

        var sha1 = a.Sha1 ?? b.Sha1;
        if (a.Sha1 is not null && b.Sha1 is not null && !string.Equals(a.Sha1, b.Sha1, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Warn("download", "conflicting-digest", ("target", a.TargetPath), ("a", a.Sha1), ("b", b.Sha1));
        }

        return a with
        {
            Urls = urls,
            Sha1 = sha1,
            Sha256 = a.Sha256 ?? b.Sha256,
            Size = a.Size > 0 ? a.Size : b.Size,
        };
    }

    private sealed class RunState(IProgress<DownloadProgress>? progress)
    {
        private readonly SpeedMeter _speed = new();
        private readonly CancellationTokenSource _reporterCts = new();
        private long _downloadedBytes;
        private int _completedFiles;
        private int _skippedFiles;
        private string? _currentFile;
        private Task? _reporter;
        private long _precompletedBytes;

        public int TotalFiles { get; set; }
        public long TotalBytes { get; set; }
        public int SkippedFiles => _skippedFiles;
        public int CompletedFilesView => _completedFiles;
        public long DownloadedBytes => _downloadedBytes + _precompletedBytes;
        public ConcurrentBag<DownloadFailure> Failures { get; } = [];

        public void OnBytesTransferred(int count)
        {
            Interlocked.Add(ref _downloadedBytes, count);
            _speed.Add(count);
        }

        public void OnSkipped(long size)
        {
            Interlocked.Increment(ref _skippedFiles);
            Interlocked.Increment(ref _completedFiles);
            if (size > 0) Interlocked.Add(ref _precompletedBytes, size);
        }

        public void OnCompleted(long size) => Interlocked.Increment(ref _completedFiles);

        public void SetCurrentFile(string name) => _currentFile = name;

        public void StartReporterIfNeeded()
        {
            if (progress is null) return;

            _reporter = Task.Run(async () =>
            {
                try
                {
                    while (!_reporterCts.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(200), _reporterCts.Token).ConfigureAwait(false);
                        Report();
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        public void StopReporter()
        {
            try
            {
                _reporterCts.Cancel();
                _reporter?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
        }

        /// <summary>Publishes the final state (100%) even when the batch finished very quickly.</summary>
        public void ReportFinal()
        {
            if (progress is null) return;
            progress.Report(new DownloadProgress(
                TotalFiles,
                Volatile.Read(ref _completedFiles),
                TotalBytes,
                DownloadedBytes,
                _speed.BytesPerSecond,
                _speed.Elapsed,
                _currentFile,
                Failures.Count));
        }

        private void Report()
        {
            if (progress is null) return;

            progress.Report(new DownloadProgress(
                TotalFiles,
                Volatile.Read(ref _completedFiles),
                TotalBytes,
                DownloadedBytes,
                _speed.BytesPerSecond,
                _speed.Elapsed,
                _currentFile,
                Failures.Count));
        }
    }
}

/// <summary>HTTP status failure with a hint about whether retrying can ever help.</summary>
public sealed class HttpStatusException(HttpStatusCode statusCode, TimeSpan? retryAfter)
    : HttpRequestException($"服务器返回 {(int)statusCode} {statusCode}")
{
    public new HttpStatusCode StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public bool IsPermanent => StatusCode is HttpStatusCode.BadRequest
        or HttpStatusCode.Unauthorized
        or HttpStatusCode.Forbidden
        or HttpStatusCode.NotFound
        or HttpStatusCode.Gone;
}
