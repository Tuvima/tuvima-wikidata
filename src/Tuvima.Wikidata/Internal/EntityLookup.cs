using Tuvima.Wikidata.Internal.Json;

namespace Tuvima.Wikidata.Internal;

/// <summary>
/// Per-request memo of referenced entities (chained-path intermediates, item values whose labels
/// are scored). Lets scoring fetch each layer once for all candidates instead of once per candidate.
/// Not thread-safe; owned by a single reconciliation request.
/// </summary>
internal sealed class EntityLookup
{
    private readonly WikidataEntityFetcher _fetcher;
    private readonly string _language;
    private readonly Dictionary<string, WikidataEntity?> _entities = new(StringComparer.OrdinalIgnoreCase);

    public EntityLookup(WikidataEntityFetcher fetcher, string language)
    {
        _fetcher = fetcher;
        _language = language;
    }

    /// <summary>Fetches any IDs not yet known in a single batched call.</summary>
    public async Task PrefetchAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        var missing = ids
            .Where(id => !string.IsNullOrWhiteSpace(id) && !_entities.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count == 0)
            return;

        var fetched = await _fetcher.FetchEntitiesAsync(missing, _language, cancellationToken).ConfigureAwait(false);
        foreach (var id in missing)
            _entities[id] = fetched.TryGetValue(id, out var entity) ? entity : null;
    }

    /// <summary>Returns the known entities for <paramref name="ids"/>, fetching any that are missing.</summary>
    public async Task<List<WikidataEntity>> GetAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        await PrefetchAsync(ids, cancellationToken).ConfigureAwait(false);
        var result = new List<WikidataEntity>(ids.Count);
        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_entities.TryGetValue(id, out var entity) && entity is not null)
                result.Add(entity);
        }
        return result;
    }

    public WikidataEntity? TryGet(string id)
        => _entities.TryGetValue(id, out var entity) ? entity : null;
}
