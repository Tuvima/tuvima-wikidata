# Configuration

## WikidataReconcilerOptions

```csharp
var reconciler = new WikidataReconciler(new WikidataReconcilerOptions
{
    // API endpoint (default: Wikidata)
    ApiEndpoint = "https://www.wikidata.org/w/api.php",

    // Search language (default: "en", overridable per-request)
    Language = "en",

    // User-Agent header. Wikimedia policy asks every app to identify itself with a contact.
    // Default: WikidataReconcilerOptions.DefaultUserAgent = "Tuvima.Wikidata/{version} (repo URL)"
    UserAgent = "MyApp/1.0 (contact@example.com)",

    // HTTP timeout (default: 30 seconds)
    Timeout = TimeSpan.FromSeconds(30),

    // Type property (default: "P31" — custom Wikibase may use different IDs)
    TypePropertyId = "P31",

    // Scoring tuning
    PropertyWeight = 0.4,        // weight for each property match (label = 1.0)
    AutoMatchThreshold = 95,     // minimum score for auto-match
    AutoMatchScoreGap = 10,      // minimum gap over second-best candidate

    // Resilience
    MaxRetries = 3,                         // retry attempts for transient 408/429/5xx failures
    RetryBaseDelay = TimeSpan.FromSeconds(1),
    MaxRetryDelay = TimeSpan.FromSeconds(30),
    RetryJitterRatio = 0.2,
    MaxLag = 5,                             // sent to Wikidata API requests

    // Provider-safe host limits
    WikidataRateLimit = new ProviderRateLimitOptions
    {
        MaxConcurrentRequests = 1,
        RequestsPerSecond = 1,
        MaxBatchSize = 50
    },
    WikipediaRateLimit = new ProviderRateLimitOptions
    {
        MaxConcurrentRequests = 2,
        RequestsPerSecond = 2,
        MaxBatchSize = 50
    },

    // Maximum active work items per batch/fan-out (host HTTP limits remain separate)
    MaxConcurrency = 5,

    // Shared pipeline features
    EnableRequestCoalescing = true,
    EnableResponseCaching = true,
    ResponseCache = new InMemoryWikidataResponseCache(),
    ResponseCacheTtl = TimeSpan.FromHours(12),

    // Type hierarchy (P279 subclass walking)
    TypeHierarchyDepth = 0,      // 0 = direct P31 match only (fast)
                                  // 5 = walk up to 5 levels of P279

    // Display-friendly labels
    IncludeSitelinkLabels = false,  // include Wikipedia sitelink titles in scoring
});
```

## Bring Your Own HttpClient

For connection pooling, custom handlers, or dependency injection:

```csharp
var httpClient = httpClientFactory.CreateClient("Wikidata");
using var reconciler = new WikidataReconciler(httpClient, options);
```

When you pass your own `HttpClient`, the reconciler will not dispose it. When the reconciler creates its own, it owns and disposes the client.

Every service owned by a `WikidataReconciler` shares one internal HTTP pipeline. The pipeline applies host throttling, retries, `Retry-After`, maxlag, cache lookup/store, request coalescing, logging, and diagnostics consistently across reconciliation, entity fetching, Wikipedia, Stage 2, and ASP.NET batch paths.

`WikidataRateLimit`, `WikipediaRateLimit`, `CommonsRateLimit`, and `DefaultRateLimit` configure independent per-host limiters. Wikidata defaults to a conservative single-flight / low-RPS policy. Each `*.wikipedia.org` language host gets its own limiter using `WikipediaRateLimit`.

`MaxRetries` caps retry attempts. If a provider sends `Retry-After`, that duration is used. Otherwise the pipeline uses exponential backoff from `RetryBaseDelay`, capped by `MaxRetryDelay`, with `RetryJitterRatio` extra jitter.

## Provider Failures, Timeouts, and Cancellation

HTTP 200 responses are checked for MediaWiki `error` and `errors` envelopes before use or caching. `maxlag` and `ratelimited` errors are retried as `RateLimited`; `readonly`, `internal_api_error_DBConnectionError`, and `internal_api_error_DBQueryTimeoutError` are retried as `TransientNetworkFailure`. Missing-entity errors become `NotFound`. Other API errors and permanent HTTP 4xx rejections become `ProviderRejected` without retries. Exhausted provider failures throw `WikidataProviderException` with the corresponding kind.

`Timeout` limits each HTTP attempt, including headers and the full response body. It starts after host admission and excludes queue time and retry delays. A supplied `HttpClient.Timeout` takes precedence when shorter; `Timeout.InfiniteTimeSpan` disables the corresponding limit. Caller cancellation propagates as `OperationCanceledException`; an exhausted attempt timeout is a `TransientNetworkFailure`.

Coalesced requests have independent caller cancellation. Cancelling either caller stops only that caller's wait while another waiter remains. The last waiter leaving cancels shared work and allows a subsequent call to start fresh. Disposing the reconciler cancels active work; limiter resources are released after active operations finish.

## Caching

The shared pipeline includes a response-cache abstraction:

```csharp
public sealed class SqliteWikidataResponseCache : IWikidataResponseCache
{
    public ValueTask<string?> GetAsync(
        WikidataResponseCacheKey key,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException(); // load raw JSON by key

    public ValueTask SetAsync(
        WikidataResponseCacheKey key,
        string response,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException(); // store raw JSON
}

using var reconciler = new WikidataReconciler(new WikidataReconcilerOptions
{
    ResponseCache = new SqliteWikidataResponseCache(),
    ResponseCacheTtl = TimeSpan.FromHours(24)
});
```

The default is `InMemoryWikidataResponseCache`. Cache keys are canonicalized so equivalent request shapes coalesce across parameter ordering where safe. The built-in cache policy covers successful entity/property/label/sitelink responses, Wikipedia summary batches, and Commons-capable responses.

The parameterless cache constructor limits storage to 1,024 entries and 64 MiB of UTF-16 key and response payloads. Object and indexing overhead are additional; this is not a whole-process memory limit. Configure both limits through `ResponseCache`, for example:

```csharp
ResponseCache = new InMemoryWikidataResponseCache(
    maxEntries: 2048,
    maxSizeBytes: 128L * 1024 * 1024)
```

Both limits must be positive. The cache evicts least-recently-used entries to make room and prunes expired entries on reads and writes, even when a different key is accessed. Responses larger than the byte limit are returned to the caller but not cached. Replacing a key first removes the old entry; a nonpositive TTL or oversized replacement leaves that key uncached. Eviction affects reuse only and can increase provider requests when the working set exceeds the configured capacity. Custom `IWikidataResponseCache` implementations retain their own storage policies.

Set `EnableResponseCaching = false` to disable cache lookup/store while keeping throttling and retry behavior.

Provider error envelopes, malformed JSON, and invalid field types in recognized response models are rejected before caching. Existing invalid cache entries are treated as misses and replaced after a successful provider response.

`CachingDelegatingHandler` (an `HttpClient` handler-level cache) is obsolete since v3.10. It sits below the retry pipeline; it now skips MediaWiki error envelopes and non-JSON bodies, but `ResponseCache` is the supported cache.

## Batch Execution and Request Pacing

`MaxConcurrency` (default 5, clamped internally to 1..1024) limits active work items per batch or variable-size fan-out. This applies to reconciliation batches/streams, multi-author resolution, person batches, Wikipedia language/article/section fan-out, and multilingual search. Person batches also retain their existing cap of three or the configured Wikidata concurrency, whichever is smaller. Nested operations have independent windows; shared host policies still cap actual HTTP concurrency across the reconciler.

Collected batches preserve input order. Reconciliation streams yield completed items with their original indices and only schedule more work as the consumer advances. Failure, caller cancellation, or early enumerator disposal cancels and observes pending operations. The execution window stays bounded even for large inputs; caller inputs and collected results still require memory proportional to the batch size.

Host pacing uses monotonic time after acquiring a concurrency slot. This spaces admissions when a backlog clears, and cancelled waits do not accumulate future reservations. Pacing and concurrency queue time remain outside the HTTP attempt timeout. `RequestsPerSecond = 0` disables pacing while preserving the host concurrency limit.

## Diagnostics and Logging

```csharp
using var reconciler = new WikidataReconciler(new WikidataReconcilerOptions
{
    RequestLogger = entry =>
        Console.WriteLine($"{entry.Host} {entry.Endpoint} {entry.StatusCode} {entry.Latency}")
});

// ...run ingestion...

var snapshot = reconciler.Diagnostics.GetSnapshot();
Console.WriteLine($"Wikidata requests: {snapshot.RequestCountByHost["www.wikidata.org"]}");
Console.WriteLine($"Cache hits: {snapshot.CacheHits}, misses: {snapshot.CacheMisses}");
Console.WriteLine($"429s: {snapshot.RateLimitResponses}, retries: {snapshot.RetryCount}");
```

`RecentFailures` and `FailuresByKind` use `WikidataFailureKind` values such as `NoSitelink`, `RateLimited`, `MalformedResponse`, and `ProviderRejected`, so consumers do not need to parse exception strings.

## OpenTelemetry (v3.10)

The library emits traces and metrics through BCL `System.Diagnostics` types, so no telemetry package is needed:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(WikidataTelemetry.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(WikidataTelemetry.MeterName));
```

- **Traces:** one `Client` span per provider request, named `wikidata {endpoint}`, tagged with `server.address`, `tuvima.wikidata.endpoint`, `url.full`, `tuvima.wikidata.attempts`, `tuvima.wikidata.cache_hit`, `http.response.status_code`, and `error.type` on failure. Retried attempts add an `attempt.failed` event.
- **Metrics:** `tuvima.wikidata.requests` (counter: attempts and cache hits, tagged by host, endpoint, cache hit, and outcome) and `tuvima.wikidata.request.duration` (histogram, seconds, cache hits excluded).

`RequestLogger` and `Diagnostics` remain available alongside.

## Per-request Type Hierarchy Depth

`ReconciliationRequest.TypeHierarchyDepth` overrides the global depth for both required and excluded types, in text searches and exact-QID lookups. `null` inherits the global setting; `0` explicitly requests direct type matching only. Positive values limit the number of P279 edges traversed. The hierarchy cache stores direct parents, so an earlier shallow lookup or early match cannot truncate a later traversal. Since v3.10, requests that opt in with a positive depth while the global depth is 0 share one cache per reconciler.

## Custom Wikibase Instances

The library works with any Wikibase instance, not just Wikidata:

```csharp
var reconciler = new WikidataReconciler(new WikidataReconcilerOptions
{
    ApiEndpoint = "https://my-wikibase.example.com/w/api.php",
    TypePropertyId = "P1",  // your instance's "instance of" property
});
```
