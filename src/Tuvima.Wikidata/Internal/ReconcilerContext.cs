namespace Tuvima.Wikidata.Internal;

/// <summary>
/// Internal shared state for the reconciler facade and its sub-services.
/// Owns the HttpClient and all collaborator instances that sub-services need.
/// </summary>
internal sealed class ReconcilerContext
{
    public HttpClient HttpClient { get; }
    public WikidataReconcilerOptions Options { get; }
    public WikidataDiagnostics Diagnostics { get; }
    public ResilientHttpClient ResilientClient { get; }
    public WikidataSearchClient SearchClient { get; }
    public WikidataEntityFetcher EntityFetcher { get; }
    public ReconciliationScorer Scorer { get; }
    public TypeChecker TypeChecker { get; }
    public SubclassResolver? SubclassResolver { get; }

    /// <summary>
    /// Shared resolver for requests that opt into P279 walking via a per-request depth while the
    /// global depth is 0. Shared so its parent cache survives across requests and batch items.
    /// </summary>
    public SubclassResolver OnDemandSubclassResolver { get; }

    public ReconcilerContext(HttpClient httpClient, WikidataReconcilerOptions options)
    {
        HttpClient = httpClient;
        Options = options;
        Diagnostics = new WikidataDiagnostics();

        ResilientClient = new ResilientHttpClient(httpClient, options, Diagnostics);
        SearchClient = new WikidataSearchClient(ResilientClient, options);
        EntityFetcher = new WikidataEntityFetcher(ResilientClient, options, Diagnostics);
        Scorer = new ReconciliationScorer(options);
        TypeChecker = new TypeChecker(options.TypePropertyId);
        SubclassResolver = options.TypeHierarchyDepth > 0
            ? new SubclassResolver(EntityFetcher, options.TypeHierarchyDepth)
            : null;
        OnDemandSubclassResolver = SubclassResolver ?? new SubclassResolver(EntityFetcher, maxDepth: 0);
    }
}
