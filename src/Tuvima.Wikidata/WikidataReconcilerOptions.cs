namespace Tuvima.Wikidata;

/// <summary>
/// Configuration options for <see cref="WikidataReconciler"/>.
/// </summary>
public sealed class WikidataReconcilerOptions
{
    /// <summary>
    /// The MediaWiki API endpoint. Default is the Wikidata API.
    /// </summary>
    public string ApiEndpoint { get; init; } = "https://www.wikidata.org/w/api.php";

    /// <summary>
    /// Default language for search and label matching. Default is "en".
    /// Can be overridden per-request via <see cref="ReconciliationRequest.Language"/>.
    /// </summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// User-Agent header sent with API requests. The default identifies this library and its
    /// version only. Wikimedia's User-Agent policy asks every application to send its own name
    /// and a contact URL or email, so production apps should set this, for example
    /// <c>"MyApp/1.2 (https://example.org; ops@example.org) Tuvima.Wikidata/3.10"</c>.
    /// </summary>
    public string UserAgent { get; init; } = DefaultUserAgent;

    /// <summary>The default User-Agent: <c>Tuvima.Wikidata/{version} (https://github.com/Tuvima/wikidata)</c>.</summary>
    public static string DefaultUserAgent { get; } =
        $"Tuvima.Wikidata/{WikidataLibraryInfo.PackageVersion.Split('+')[0]} (https://github.com/Tuvima/wikidata)";

    /// <summary>
    /// Per-attempt timeout covering HTTP headers and response body. Default is 30 seconds.
    /// Starts after host admission; queue time and retry delays are separate.
    /// A supplied HttpClient's shorter timeout wins. InfiniteTimeSpan disables this option's limit.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The property ID used for type classification (instance of).
    /// Default is "P31" for Wikidata. Custom Wikibase instances may use a different property.
    /// </summary>
    public string TypePropertyId { get; init; } = "P31";

    /// <summary>
    /// The property weight used for scoring property matches. Default is 0.4.
    /// The label match always has weight 1.0.
    /// </summary>
    public double PropertyWeight { get; init; } = 0.4;

    /// <summary>
    /// The validation threshold for auto-matching. Default is 95.
    /// A candidate must score above (threshold - 5 * numProperties) to be an auto-match.
    /// </summary>
    public double AutoMatchThreshold { get; init; } = 95;

    /// <summary>
    /// The minimum score gap between the top candidate and the second candidate
    /// for auto-matching. Default is 10.
    /// </summary>
    public double AutoMatchScoreGap { get; init; } = 10;

    /// <summary>
    /// Maximum active work items per batch or variable-size fan-out. Default is 5.
    /// Internally clamped to 1..1024. Nested operations have independent windows;
    /// provider HTTP concurrency is separately controlled by per-host rate-limit options.
    /// </summary>
    public int MaxConcurrency { get; init; } = 5;

    /// <summary>
    /// Number of retry attempts for transient HTTP/provider failures.
    /// Retry-After is honored when present; otherwise exponential backoff with jitter is used.
    /// Default is 3.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Base delay for exponential retry backoff when Retry-After is absent.
    /// Default is 1 second.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum delay for exponential retry backoff when Retry-After is absent.
    /// Retry-After values from the provider are honored as-is.
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Jitter ratio applied to exponential backoff delays when Retry-After is absent.
    /// Default is 0.2 (up to 20% extra delay).
    /// </summary>
    public double RetryJitterRatio { get; init; } = 0.2;

    /// <summary>
    /// The maxlag parameter sent with every API request (Wikimedia bot etiquette).
    /// If the server is lagging more than this many seconds, it can return an HTTP 200
    /// maxlag error, which is retried and never cached. Default is 5 seconds.
    /// Set to 0 to disable. See https://www.mediawiki.org/wiki/Manual:Maxlag_parameter
    /// </summary>
    public int MaxLag { get; init; } = 5;

    /// <summary>
    /// Host policy for www.wikidata.org. Default is conservative: one in-flight request
    /// and one request start per second.
    /// </summary>
    public ProviderRateLimitOptions WikidataRateLimit { get; init; } = new()
    {
        MaxConcurrentRequests = 1,
        RequestsPerSecond = 1,
        MaxBatchSize = 50
    };

    /// <summary>
    /// Host policy for wikipedia.org API hosts. Each language host gets its own limiter.
    /// </summary>
    public ProviderRateLimitOptions WikipediaRateLimit { get; init; } = new()
    {
        MaxConcurrentRequests = 2,
        RequestsPerSecond = 2,
        MaxBatchSize = 50
    };

    /// <summary>
    /// Host policy for commons.wikimedia.org.
    /// </summary>
    public ProviderRateLimitOptions CommonsRateLimit { get; init; } = new()
    {
        MaxConcurrentRequests = 1,
        RequestsPerSecond = 1,
        MaxBatchSize = 50
    };

    /// <summary>
    /// Host policy used for other configured Wikibase or Wikimedia hosts.
    /// </summary>
    public ProviderRateLimitOptions DefaultRateLimit { get; init; } = new()
    {
        MaxConcurrentRequests = 1,
        RequestsPerSecond = 1,
        MaxBatchSize = 50
    };

    /// <summary>
    /// Enables coalescing of identical in-flight GET requests. Default is true.
    /// Each caller can cancel its own wait independently. Shared work is cancelled when
    /// the last waiter leaves or the reconciler is disposed.
    /// </summary>
    public bool EnableRequestCoalescing { get; init; } = true;

    /// <summary>
    /// Enables raw response caching for cacheable entity, sitelink, summary, and Commons responses.
    /// Default is true.
    /// </summary>
    public bool EnableResponseCaching { get; init; } = true;

    /// <summary>
    /// Cache used by the shared HTTP pipeline. Defaults to a process-local LRU cache
    /// limited to 1,024 entries and 64 MiB of UTF-16 key and response payloads.
    /// Applications can replace this with a durable provider cache.
    /// </summary>
    public IWikidataResponseCache? ResponseCache { get; init; } = new InMemoryWikidataResponseCache();

    /// <summary>
    /// Time-to-live for successful cacheable provider responses. Default is 12 hours.
    /// </summary>
    public TimeSpan ResponseCacheTtl { get; init; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Optional callback invoked for provider requests, retries, cache hits, and failures.
    /// </summary>
    public Action<WikidataHttpLogEntry>? RequestLogger { get; init; }

    /// <summary>
    /// Optional callback invoked by high-level batch operations to report item and phase progress.
    /// </summary>
    public Action<WikidataProgressEvent>? ProgressReporter { get; init; }

    /// <summary>
    /// Maximum depth for P279 (subclass of) hierarchy walking during type checking.
    /// Default is 0 (direct P31 match only — fast, no extra API calls).
    /// Set to a positive value (e.g., 5) to walk superclasses and improve type recall.
    /// For example, with depth 3, a "novel" (Q8261) entity would match a query
    /// for "literary work" (Q7725634) because novel → literary work via P279.
    /// </summary>
    public int TypeHierarchyDepth { get; init; } = 0;

    /// <summary>
    /// When true, Wikipedia sitelink titles are included in the label pool for scoring.
    /// Sitelink titles are often more human-friendly than formal Wikidata labels
    /// (e.g., "Frankenstein" instead of "Frankenstein; or, The Modern Prometheus").
    /// Default is false (opt-in to avoid increased API response size).
    /// </summary>
    public bool IncludeSitelinkLabels { get; init; } = false;

    /// <summary>
    /// Property IDs considered unique identifiers. When a property constraint matches
    /// one of these with score 100, the overall reconciliation score is set to 100.
    /// Default includes common authority control IDs.
    /// Set to an empty set to disable the shortcut.
    /// </summary>
    public IReadOnlySet<string> UniqueIdProperties { get; init; } = new HashSet<string>
    {
        "P213",  // ISNI
        "P214",  // VIAF ID
        "P227",  // GND ID
        "P244",  // Library of Congress authority ID
        "P268",  // BnF ID
        "P269",  // IdRef ID
        "P349",  // National Diet Library ID
        "P496",  // ORCID iD
        "P906",  // SELIBR ID
        "P1006", // NTA ID (Netherlands)
        "P1015", // NORAF ID
        "P1566", // GeoNames ID
        "P2427", // GRID ID
    };
}
