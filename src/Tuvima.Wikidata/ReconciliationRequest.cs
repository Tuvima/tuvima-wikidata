namespace Tuvima.Wikidata;

/// <summary>
/// A request to reconcile a text query against Wikidata entities.
/// </summary>
public sealed class ReconciliationRequest
{
    /// <summary>
    /// The text query to reconcile (e.g., "Douglas Adams").
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Optional list of acceptable types (OR logic). Candidates whose P31 matches
    /// ANY of these types pass the filter. Also enables CirrusSearch type filtering
    /// at query time for better recall.
    /// </summary>
    public IReadOnlyList<string>? Types { get; init; }

    /// <summary>
    /// Optional types to exclude from results. Candidates whose P31 includes
    /// any of these types will be removed.
    /// </summary>
    public IReadOnlyList<string>? ExcludeTypes { get; init; }

    /// <summary>
    /// Per-request override for P279 subclass hierarchy walk depth.
    /// If null, uses the global <see cref="WikidataReconcilerOptions.TypeHierarchyDepth"/>.
    /// Zero disables hierarchy walking even when the global depth is positive.
    /// Applies to both required and excluded types, including exact-QID lookups.
    /// </summary>
    public int? TypeHierarchyDepth { get; init; }

    /// <summary>Maximum number of results to return. Default is 5.</summary>
    public int Limit { get; init; } = 5;

    /// <summary>
    /// Language code for search and label matching (e.g., "en", "de", "fr").
    /// If null, uses the language from <see cref="WikidataReconcilerOptions"/>.
    /// For multi-language search, use <see cref="Languages"/> instead.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// When set, searches run concurrently in each language and results are merged by QID.
    /// The first language in the list is used for label/description display.
    /// Takes precedence over <see cref="Language"/> for search.
    /// </summary>
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary>Optional property constraints to improve matching accuracy.</summary>
    public IReadOnlyList<PropertyConstraint>? Properties { get; init; }

    /// <summary>
    /// When true, search and scoring normalize diacritics so that "Shōgun" matches "Shogun".
    /// </summary>
    public bool DiacriticInsensitive { get; init; }

    /// <summary>
    /// When true and <see cref="Types"/> is set, the plain full-text search is skipped: the autocomplete search and the
    /// type-filtered full-text search (which only returns entities of the requested types) already cover the candidates,
    /// so the unfiltered full-text search would mostly return things of other types. Saves one request per query. Use it for
    /// types every entity carries directly, such as people (Q5). Leave it off for types with many subclasses.
    /// </summary>
    public bool TypeFilteredSearchOnly { get; init; }

    /// <summary>
    /// Optional pipeline of text transformations applied to the query before search.
    /// </summary>
    public IReadOnlyList<Func<string, string>>? Cleaners { get; init; }
}
