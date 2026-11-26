using System.Text.RegularExpressions;
using Tuvima.Wikidata.Internal.Json;

namespace Tuvima.Wikidata.Internal;

/// <summary>
/// Searches Wikidata using both wbsearchentities (label/alias autocomplete) and
/// action=query&amp;list=search (full-text search), then merges results.
/// </summary>
internal sealed class WikidataSearchClient
{
    private static readonly Regex QidPattern = new(@"^Q\d+$", RegexOptions.Compiled);

    private readonly ResilientHttpClient _httpClient;
    private readonly WikidataReconcilerOptions _options;

    public WikidataSearchClient(ResilientHttpClient httpClient, WikidataReconcilerOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    /// <summary>
    /// Searches for candidate entities matching the query.
    /// Returns deduplicated entity IDs (full-text results first, then autocomplete results).
    /// </summary>
    public async Task<List<string>> SearchAsync(string query, string language, int limit,
        bool diacriticInsensitive = false, CancellationToken cancellationToken = default)
    {
        // Direct QID lookup bypass
        if (QidPattern.IsMatch(query.Trim()))
            return [query.Trim().ToUpperInvariant()];

        // Truncate long queries to avoid silent empty results (upstream issue #116)
        var searchQuery = query.Length > 250 ? query[..250] : query;
        var fetchLimit = Math.Min(2 * limit, 50);

        // Run both searches concurrently
        var tasks = new List<Task<List<string>>>
        {
            SearchEntitiesAsync(searchQuery, language, fetchLimit, cancellationToken),
            FullTextSearchAsync(searchQuery, fetchLimit, cancellationToken)
        };

        // If diacritic-insensitive and the stripped query differs, run additional searches
        if (diacriticInsensitive)
        {
            var stripped = FuzzyMatcher.RemoveDiacritics(searchQuery);
            if (!string.Equals(stripped, searchQuery, StringComparison.Ordinal))
            {
                tasks.Add(SearchEntitiesAsync(stripped, language, fetchLimit, cancellationToken));
                tasks.Add(FullTextSearchAsync(stripped, fetchLimit, cancellationToken));
            }
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Merge: full-text results first (index 1, then 3 if present), then autocomplete (0, then 2)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        // Full-text results first
        AddUnique(await tasks[1].ConfigureAwait(false), seen, merged);
        if (tasks.Count > 2)
            AddUnique(await tasks[3].ConfigureAwait(false), seen, merged);

        // Then autocomplete results
        AddUnique(await tasks[0].ConfigureAwait(false), seen, merged);
        if (tasks.Count > 2)
            AddUnique(await tasks[2].ConfigureAwait(false), seen, merged);

        return merged;
    }

    private static void AddUnique(List<string> ids, HashSet<string> seen, List<string> merged)
    {
        foreach (var id in ids)
        {
            if (seen.Add(id))
                merged.Add(id);
        }
    }

    /// <summary>
    /// Searches in multiple languages concurrently, merging and deduplicating results by QID.
    /// </summary>
    public async Task<List<string>> SearchMultiLanguageAsync(
        string query, IReadOnlyList<string> languages, int limit,
        bool diacriticInsensitive = false, CancellationToken cancellationToken = default)
    {
        if (QidPattern.IsMatch(query.Trim()))
            return [query.Trim().ToUpperInvariant()];

        var effectiveLanguages = languages
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (effectiveLanguages.Count == 0)
            return await SearchAsync(query, _options.Language, limit, diacriticInsensitive, cancellationToken)
                .ConfigureAwait(false);

        var searchQuery = query.Length > 250 ? query[..250] : query;
        var fetchLimit = Math.Min(2 * limit, 50);

        var queries = new List<string> { searchQuery };
        if (diacriticInsensitive)
        {
            var stripped = FuzzyMatcher.RemoveDiacritics(searchQuery);
            if (!string.Equals(stripped, searchQuery, StringComparison.Ordinal))
                queries.Add(stripped);
        }

        // Preserve full-text-first merge order without starting a task for every language.
        var searches = queries.Select(q => (Query: q, Language: (string?)null)).ToList();
        searches.AddRange(queries.SelectMany(q => effectiveLanguages.Select(lang => (q, (string?)lang))));
        var results = await BoundedAsync.SelectAsync(searches, _options.MaxConcurrency,
            (search, token) => search.Language is null
                ? FullTextSearchAsync(search.Query, fetchLimit, token)
                : SearchEntitiesAsync(search.Query, search.Language, fetchLimit, token), cancellationToken)
            .ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        foreach (var result in results)
            AddUnique(result, seen, merged);

        return merged;
    }

    /// <summary>
    /// Lightweight autocomplete search for type-ahead scenarios.
    /// Returns label, description, and ID for each match.
    /// </summary>
    public async Task<List<WbSearchResult>> SuggestAsync(string prefix, string language, int limit, CancellationToken cancellationToken = default)
    {
        var url = $"{_options.ApiEndpoint}?action=wbsearchentities&search={Uri.EscapeDataString(prefix)}" +
                  $"&language={Uri.EscapeDataString(language)}&limit={limit}&format=json";

        var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var response = ProviderJson.Deserialize(json, WikidataJsonContext.Default.WbSearchEntitiesResponse, "wbsearchentities");

        return response?.Search ?? [];
    }

    /// <summary>
    /// Lightweight autocomplete search for Wikidata properties.
    /// Uses wbsearchentities with type=property.
    /// </summary>
    public async Task<List<WbSearchResult>> SuggestPropertiesAsync(string prefix, string language, int limit, CancellationToken cancellationToken = default)
    {
        var url = $"{_options.ApiEndpoint}?action=wbsearchentities&search={Uri.EscapeDataString(prefix)}" +
                  $"&language={Uri.EscapeDataString(language)}&type=property&limit={limit}&format=json";

        var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var response = ProviderJson.Deserialize(json, WikidataJsonContext.Default.WbSearchEntitiesResponse, "wbsearchentities");

        return response?.Search ?? [];
    }

    /// <summary>The autocomplete search alone (wbsearchentities), without the full-text search.</summary>
    public async Task<List<string>> SearchAutocompleteAsync(string query, string language, int limit,
        CancellationToken cancellationToken = default)
    {
        if (QidPattern.IsMatch(query.Trim()))
            return [query.Trim().ToUpperInvariant()];

        var searchQuery = query.Length > 250 ? query[..250] : query;
        return await SearchEntitiesAsync(searchQuery, language, Math.Min(2 * limit, 50), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches for entities with CirrusSearch type filtering (haswbstatement:P31=QID).
    /// Returns entities whose P31 matches any of the provided types. By default each type is searched in turn, in the order given,
    /// so earlier types rank first. With <paramref name="singleRequest"/> all types go in one request
    /// (<c>haswbstatement:P31=A OR haswbstatement:P31=B</c>), which costs one request however many types are allowed
    /// but ranks them together.
    /// </summary>
    public async Task<List<string>> SearchWithTypeFilterAsync(
        string query, string language, IReadOnlyList<string> typeQids, int limit,
        CancellationToken cancellationToken = default, bool singleRequest = false)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        var types = typeQids.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var filters = singleRequest && types.Count > 1
            ? [$"({string.Join(" OR ", types.Select(t => $"haswbstatement:P31={t}"))})"]
            : types.Select(t => $"haswbstatement:P31={t}").ToList();

        foreach (var filter in filters)
        {
            var searchQuery = $"{query} {filter}";
            var url = $"{_options.ApiEndpoint}?action=query&list=search&srnamespace=0" +
                      $"&srlimit={limit}&srsearch={Uri.EscapeDataString(searchQuery)}&format=json";

            var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var response = ProviderJson.Deserialize(json, WikidataJsonContext.Default.QuerySearchResponse, "query.search");
            var ids = response?.Query?.Search?.Select(r => r.Title) ?? [];
            foreach (var id in ids)
            {
                if (seen.Add(id))
                    merged.Add(id);
            }
        }

        return merged.Count > limit ? merged.Take(limit).ToList() : merged;
    }

    /// <summary>
    /// Searches for all entities matching a haswbstatement filter with pagination.
    /// Follows continue tokens to collect all results up to 500 per page.
    /// Optionally filters by P31 type.
    /// </summary>
    public async Task<List<string>> SearchAllByStatementAsync(
        string query, IReadOnlyList<string>? typeFilter = null,
        CancellationToken cancellationToken = default)
    {
        var searchQuery = typeFilter is { Count: > 0 }
            ? $"{query} ({string.Join(" OR ", typeFilter.Select(t => $"haswbstatement:P31={t}"))})"
            : query;

        var allResults = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? sroffset = 0;

        while (sroffset is not null)
        {
            var url = $"{_options.ApiEndpoint}?action=query&list=search&srnamespace=0" +
                      $"&srlimit=500&srsearch={Uri.EscapeDataString(searchQuery)}&format=json";
            if (sroffset > 0)
                url += $"&sroffset={sroffset}";

            var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var response = ProviderJson.Deserialize(json, WikidataJsonContext.Default.QuerySearchResponse, "query.search");

            if (response?.Query?.Search is { Count: > 0 } results)
            {
                foreach (var r in results)
                {
                    if (seen.Add(r.Title))
                        allResults.Add(r.Title);
                }
            }

            // Check for continuation
            sroffset = response?.Continue?.SrOffset;
        }

        return allResults;
    }

    /// <summary>
    /// Searches for entities by an external ID property value using haswbstatement.
    /// </summary>
    public async Task<List<string>> SearchByExternalIdAsync(
        string propertyId, string value, int limit, CancellationToken cancellationToken = default)
    {
        // Use CirrusSearch haswbstatement filter for exact property-value lookup
        var query = $"haswbstatement:{propertyId}={value}";
        var url = $"{_options.ApiEndpoint}?action=query&list=search&srnamespace=0" +
                  $"&srlimit={limit}&srsearch={Uri.EscapeDataString(query)}&format=json";

        var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var response = ProviderJson.Deserialize(json, WikidataJsonContext.Default.QuerySearchResponse, "query.search");

        return response?.Query?.Search?.Select(r => r.Title).ToList() ?? [];
    }

    private async Task<List<string>> SearchEntitiesAsync(string query, string language, int limit, CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiEndpoint}?action=wbsearchentities&search={Uri.EscapeDataString(query)}" +
                  $"&language={Uri.EscapeDataString(language)}&limit={limit}&format=json";

        var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var response = ProviderJson.Deserialize(json, WikidataJsonContext.Default.WbSearchEntitiesResponse, "wbsearchentities");

        return response?.Search?.Select(r => r.Id).ToList() ?? [];
    }

    private async Task<List<string>> FullTextSearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiEndpoint}?action=query&list=search&srnamespace=0" +
                  $"&srlimit={limit}&srsearch={Uri.EscapeDataString(query)}&srwhat=text&format=json";

        var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var response = ProviderJson.Deserialize(json, WikidataJsonContext.Default.QuerySearchResponse, "query.search");

        return response?.Query?.Search?.Select(r => r.Title).ToList() ?? [];
    }
}
