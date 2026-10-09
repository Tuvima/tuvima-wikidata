using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Tuvima.Wikidata.Internal;

/// <summary>
/// Shared Wikimedia HTTP pipeline: per-host throttling, retry/backoff, Retry-After,
/// response caching, in-flight coalescing, request logging, and diagnostics.
/// </summary>
internal sealed class ResilientHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly WikidataReconcilerOptions _options;
    private readonly WikidataDiagnostics _diagnostics;
    private readonly HostRateLimiterRegistry _hostLimiters;
    private readonly object _requestGate = new();
    private readonly Dictionary<string, SharedRequest> _inFlight = new(StringComparer.Ordinal);
    private readonly HashSet<SharedRequest> _activeRequests = [];
    private bool _disposed;
    private bool _limitersDisposed;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim? _legacyConcurrencyLimiter;

    public ResilientHttpClient(
        HttpClient httpClient,
        WikidataReconcilerOptions options,
        WikidataDiagnostics diagnostics,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _httpClient = httpClient;
        _options = options;
        _diagnostics = diagnostics;
        _hostLimiters = new HostRateLimiterRegistry(options, diagnostics);
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public ResilientHttpClient(HttpClient httpClient, int maxRetries, int maxLag, SemaphoreSlim concurrencyLimiter)
        : this(
            httpClient,
            new WikidataReconcilerOptions
            {
                MaxRetries = maxRetries,
                MaxLag = maxLag,
                RetryJitterRatio = 0,
                EnableResponseCaching = false,
                WikidataRateLimit = ProviderRateLimitOptions.Unthrottled,
                WikipediaRateLimit = ProviderRateLimitOptions.Unthrottled,
                CommonsRateLimit = ProviderRateLimitOptions.Unthrottled,
                DefaultRateLimit = ProviderRateLimitOptions.Unthrottled
            },
            new WikidataDiagnostics())
    {
        _legacyConcurrencyLimiter = concurrencyLimiter;
    }

    public async Task<string> GetStringAsync(
        string url,
        CancellationToken cancellationToken,
        bool applyMaxLag = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestUrl = applyMaxLag ? AppendMaxLag(url) : url;
        var request = ProviderRequest.Create(requestUrl);
        SharedRequest shared;
        bool start;
        lock (_requestGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_options.EnableRequestCoalescing && _inFlight.TryGetValue(request.CacheKey.Key, out var existing))
            {
                shared = existing;
                shared.Waiters++;
                start = false;
                _diagnostics.RecordCoalescedRequest();
            }
            else
            {
                shared = new SharedRequest();
                _activeRequests.Add(shared);
                if (_options.EnableRequestCoalescing)
                    _inFlight.Add(request.CacheKey.Key, shared);
                start = true;
            }
        }

        if (start)
            _ = ExecuteSharedAsync(request, shared);

        try
        {
            return await shared.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _diagnostics.RecordFailure(WikidataFailureKind.Cancelled, request.Endpoint, "The caller cancelled its provider request.");
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            lock (_requestGate)
            {
                if (--shared.Waiters == 0)
                {
                    RemoveSharedRequest(request, shared);
                    if (shared.Finished)
                        shared.Cancellation.Dispose();
                    else
                        shared.Cancellation.Cancel();
                }
            }
        }
    }

    private async Task ExecuteSharedAsync(ProviderRequest request, SharedRequest shared)
    {
        string? result = null;
        Exception? failure = null;
        var token = shared.Cancellation.Token;
        try
        {
            result = await GetStringCoreAsync(request, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            // Cleanup belongs to the operation, even if every caller stopped waiting.
            lock (_requestGate)
            {
                shared.Finished = true;
                RemoveSharedRequest(request, shared);
                _activeRequests.Remove(shared);
                if (shared.Waiters == 0)
                    shared.Cancellation.Dispose();
                DisposeLimitersIfIdle();
            }
        }

        if (failure is OperationCanceledException)
            shared.Completion.TrySetCanceled(token);
        else if (failure is not null)
        {
            shared.Completion.TrySetException(failure);
            // Observe failures when all callers have cancelled their waits.
            _ = shared.Completion.Task.Exception;
        }
        else
            shared.Completion.TrySetResult(result!);
    }

    private void RemoveSharedRequest(ProviderRequest request, SharedRequest shared)
    {
        if (_inFlight.TryGetValue(request.CacheKey.Key, out var current) && ReferenceEquals(current, shared))
            _inFlight.Remove(request.CacheKey.Key);
    }

    private async Task<string> GetStringCoreAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        using var activity = WikidataTelemetry.ActivitySource.StartActivity(
            "wikidata " + request.Endpoint, ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("server.address", request.Host);
            activity.SetTag("tuvima.wikidata.endpoint", request.Endpoint);
            activity.SetTag("url.full", request.Uri.ToString());
        }

        try
        {
            return await GetStringWithRetriesAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (activity is not null)
        {
            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity.SetTag("error.type", ex is WikidataProviderException provider ? provider.Kind.ToString() : ex.GetType().Name);
            throw;
        }
    }

    private async Task<string> GetStringWithRetriesAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        var cache = _options.EnableResponseCaching && request.IsCacheable()
            ? _options.ResponseCache
            : null;

        if (cache is not null)
        {
            var cached = await cache.GetAsync(request.CacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null && ProviderJson.IsValidCachedResponse(cached, request.Endpoint))
            {
                _diagnostics.RecordCacheHit();
                Log(request, attempt: 0, statusCode: null, latency: TimeSpan.Zero, fromCache: true, failureKind: null);
                return cached;
            }

            _diagnostics.RecordCacheMiss();
        }

        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? retryDelay = null;
            var stopwatch = Stopwatch.StartNew();
            HttpStatusCode? statusCode = null;
            RetryConditionHeaderValue? retryAfter = null;

            try
            {
                using var limiterLease = await WaitForLimiterAsync(request.Host, cancellationToken)
                    .ConfigureAwait(false);

                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // HttpClient.Timeout ends at the headers when ResponseHeadersRead is used.
                // Preserve a caller-supplied client's shorter timeout for the entire attempt.
                var timeout = _options.Timeout;
                if (_httpClient.Timeout != Timeout.InfiniteTimeSpan &&
                    (timeout == Timeout.InfiniteTimeSpan || _httpClient.Timeout < timeout))
                    timeout = _httpClient.Timeout;
                attemptCancellation.CancelAfter(timeout);
                var attemptToken = attemptCancellation.Token;

                using var response = await _httpClient
                    .GetAsync(request.Uri, HttpCompletionOption.ResponseHeadersRead, attemptToken)
                    .ConfigureAwait(false);

                statusCode = response.StatusCode;
                retryAfter = response.Headers.RetryAfter;
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    _diagnostics.RecordRateLimitResponse();

                if (ShouldRetry(response.StatusCode) && attempt < _options.MaxRetries)
                {
                    retryDelay = GetRetryDelay(response.Headers.RetryAfter, attempt);
                    _diagnostics.RecordRetry();
                    stopwatch.Stop();
                    _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                    Log(
                        request,
                        attempt,
                        statusCode,
                        stopwatch.Elapsed,
                        fromCache: false,
                        response.StatusCode == HttpStatusCode.TooManyRequests
                            ? WikidataFailureKind.RateLimited
                            : WikidataFailureKind.TransientNetworkFailure);
                }
                else if (!response.IsSuccessStatusCode)
                {
                    throw CreateStatusException(response.StatusCode, request.Uri);
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync(attemptToken).ConfigureAwait(false);
                    var providerError = ProviderJson.ValidateResponse(body, request.Endpoint);
                    attemptToken.ThrowIfCancellationRequested();
                    if (providerError is not null)
                        throw new ProviderResponseException(providerError, response.StatusCode, request.Uri);
                    stopwatch.Stop();
                    _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                    Log(request, attempt, statusCode, stopwatch.Elapsed, fromCache: false, failureKind: null);

                    if (cache is not null)
                    {
                        await cache.SetAsync(
                            request.CacheKey,
                            body,
                            _options.ResponseCacheTtl,
                            cancellationToken).ConfigureAwait(false);
                    }

                    return body;
                }
            }
            catch (ProviderResponseException ex) when (ex.Error.Retryable && attempt < _options.MaxRetries)
            {
                retryDelay = GetRetryDelay(retryAfter, attempt);
                _diagnostics.RecordRetry();
                stopwatch.Stop();
                _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                Log(request, attempt, statusCode, stopwatch.Elapsed, fromCache: false, ex.Error.Kind);
            }
            catch (ProviderResponseException ex)
            {
                var failure = new WikidataProviderException(ex.Error.Kind,
                    $"The provider rejected {request.Endpoint}: {ex.Error.Code}.", ex.StatusCode, ex.RequestUri)
                {
                    ProviderCode = ex.Error.Code,
                    MissingEntityId = ex.Error.EntityId
                };
                stopwatch.Stop();
                _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                _diagnostics.RecordFailure(failure.Kind, request.Endpoint, failure.Message);
                Log(request, attempt, statusCode, stopwatch.Elapsed, fromCache: false, failure.Kind);
                throw failure;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                _diagnostics.RecordFailure(
                    WikidataFailureKind.Cancelled,
                    request.Endpoint,
                    $"The request to {request.Host}/{request.Endpoint} was cancelled.");
                Log(request, attempt, statusCode, stopwatch.Elapsed, fromCache: false, WikidataFailureKind.Cancelled);
                throw;
            }
            catch (OperationCanceledException) when (attempt < _options.MaxRetries)
            {
                retryDelay = GetRetryDelay(retryAfter: null, attempt);
                _diagnostics.RecordRetry();
                stopwatch.Stop();
                _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                Log(request, attempt, statusCode, stopwatch.Elapsed, fromCache: false, WikidataFailureKind.TransientNetworkFailure);
            }
            catch (OperationCanceledException ex)
            {
                stopwatch.Stop();
                _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                var providerException = CreateTransientException(request.Uri, ex);
                _diagnostics.RecordFailure(providerException.Kind, request.Endpoint, providerException.Message);
                Log(request, attempt, statusCode, stopwatch.Elapsed, fromCache: false, providerException.Kind);
                throw providerException;
            }
            catch (HttpRequestException) when (attempt < _options.MaxRetries)
            {
                retryDelay = GetRetryDelay(retryAfter: null, attempt);
                _diagnostics.RecordRetry();
                stopwatch.Stop();
                _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                Log(request, attempt, statusCode, stopwatch.Elapsed, fromCache: false, WikidataFailureKind.TransientNetworkFailure);
            }
            catch (HttpRequestException ex)
            {
                stopwatch.Stop();
                _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                var providerException = CreateTransientException(request.Uri, ex);
                _diagnostics.RecordFailure(providerException.Kind, request.Endpoint, providerException.Message);
                Log(request, attempt, statusCode, stopwatch.Elapsed, fromCache: false, providerException.Kind);
                throw providerException;
            }
            catch (WikidataProviderException ex)
            {
                stopwatch.Stop();
                _diagnostics.RecordRequest(request.Host, request.Endpoint, stopwatch.Elapsed);
                _diagnostics.RecordFailure(ex.Kind, request.Endpoint, ex.Message);
                Log(request, attempt, ex.StatusCode, stopwatch.Elapsed, fromCache: false, ex.Kind);
                throw;
            }

            if (retryDelay is not null)
            {
                await _delayAsync(retryDelay.Value, cancellationToken).ConfigureAwait(false);
                continue;
            }
        }
    }

    private async ValueTask<IDisposable> WaitForLimiterAsync(string host, CancellationToken cancellationToken)
    {
        if (_legacyConcurrencyLimiter is null)
            return await _hostLimiters.WaitAsync(host, cancellationToken).ConfigureAwait(false);

        await _legacyConcurrencyLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new LegacyLease(_legacyConcurrencyLimiter);
    }

    private void Log(
        ProviderRequest request,
        int attempt,
        HttpStatusCode? statusCode,
        TimeSpan latency,
        bool fromCache,
        WikidataFailureKind? failureKind)
    {
        var outcome = failureKind?.ToString() ?? "Success";
        var tags = new TagList
        {
            { "server.address", request.Host },
            { "tuvima.wikidata.endpoint", request.Endpoint },
            { "tuvima.wikidata.cache_hit", fromCache },
            { "tuvima.wikidata.outcome", outcome }
        };
        WikidataTelemetry.Requests.Add(1, tags);
        if (!fromCache)
            WikidataTelemetry.RequestDuration.Record(latency.TotalSeconds, tags);

        if (Activity.Current is { } activity && activity.Source == WikidataTelemetry.ActivitySource)
        {
            activity.SetTag("tuvima.wikidata.cache_hit", fromCache);
            activity.SetTag("tuvima.wikidata.attempts", attempt + 1);
            if (statusCode is not null)
                activity.SetTag("http.response.status_code", (int)statusCode.Value);
            if (failureKind is not null)
                activity.AddEvent(new ActivityEvent("attempt.failed", tags: new ActivityTagsCollection
                {
                    { "tuvima.wikidata.attempt", attempt },
                    { "tuvima.wikidata.outcome", outcome }
                }));
        }

        _options.RequestLogger?.Invoke(new WikidataHttpLogEntry(
            request.Host,
            request.Endpoint,
            request.Uri,
            attempt,
            statusCode,
            latency,
            fromCache,
            failureKind));
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.RequestTimeout => true,
            HttpStatusCode.TooManyRequests => true,
            HttpStatusCode.InternalServerError => true,
            HttpStatusCode.BadGateway => true,
            HttpStatusCode.ServiceUnavailable => true,
            HttpStatusCode.GatewayTimeout => true,
            _ => false
        };
    }

    private TimeSpan GetRetryDelay(RetryConditionHeaderValue? retryAfter, int attempt)
    {
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                return delay;
        }

        var baseMilliseconds = Math.Max(1, _options.RetryBaseDelay.TotalMilliseconds);
        var exponential = baseMilliseconds * Math.Pow(2, attempt);
        var capped = Math.Min(exponential, Math.Max(baseMilliseconds, _options.MaxRetryDelay.TotalMilliseconds));

        var jitterRatio = Math.Clamp(_options.RetryJitterRatio, 0, 1);
        if (jitterRatio > 0)
            capped += Random.Shared.NextDouble() * capped * jitterRatio;

        return TimeSpan.FromMilliseconds(capped);
    }

    private static WikidataProviderException CreateStatusException(HttpStatusCode statusCode, Uri requestUri)
    {
        var kind = statusCode switch
        {
            HttpStatusCode.NotFound => WikidataFailureKind.NotFound,
            HttpStatusCode.TooManyRequests => WikidataFailureKind.RateLimited,
            HttpStatusCode.RequestTimeout => WikidataFailureKind.TransientNetworkFailure,
            >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError => WikidataFailureKind.ProviderRejected,
            _ => WikidataFailureKind.TransientNetworkFailure
        };

        return new WikidataProviderException(
            kind,
            $"The provider returned HTTP {(int)statusCode} ({statusCode}) for {requestUri.Host}.",
            statusCode,
            requestUri);
    }

    private static WikidataProviderException CreateTransientException(Uri requestUri, Exception innerException)
    {
        return new WikidataProviderException(
            WikidataFailureKind.TransientNetworkFailure,
            $"The provider request to {requestUri.Host} failed after retries were exhausted.",
            requestUri: requestUri,
            innerException: innerException);
    }

    private string AppendMaxLag(string url)
    {
        if (_options.MaxLag <= 0 || url.Contains("maxlag=", StringComparison.OrdinalIgnoreCase))
            return url;

        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}maxlag={_options.MaxLag}";
    }

    public void Dispose()
    {
        lock (_requestGate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var request in _activeRequests.ToArray())
                request.Cancellation.Cancel();
            DisposeLimitersIfIdle();
        }
    }

    private void DisposeLimitersIfIdle()
    {
        if (_disposed && !_limitersDisposed && _activeRequests.Count == 0)
        {
            _limitersDisposed = true;
            _hostLimiters.Dispose();
        }
    }

    private sealed class SharedRequest
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waiters { get; set; } = 1;
        public bool Finished { get; set; }
    }

    private sealed class ProviderResponseException(ProviderError error, HttpStatusCode statusCode, Uri requestUri) : Exception
    {
        public ProviderError Error { get; } = error;
        public HttpStatusCode StatusCode { get; } = statusCode;
        public Uri RequestUri { get; } = requestUri;
    }

    private sealed class LegacyLease : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public LegacyLease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _semaphore.Release();
        }
    }
}
