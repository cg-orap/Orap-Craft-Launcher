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
    public int MaxConcurrency { get; set; } = 8;
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
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxConcurrency),
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
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
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
