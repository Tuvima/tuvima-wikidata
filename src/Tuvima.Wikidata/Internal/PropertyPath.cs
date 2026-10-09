using System.Text.Json;
using Tuvima.Wikidata.Internal.Json;

namespace Tuvima.Wikidata.Internal;

/// <summary>
/// Resolves chained property paths like "P131/P17" (administrative territory / country).
/// For a path like "P131/P17", it first gets the P131 values (which are entity QIDs),
/// then fetches those entities and gets their P17 values.
/// </summary>
internal sealed class PropertyPath
{
    private readonly string[] _segments;

    public PropertyPath(string path)
    {
        _segments = path.Split('/');
    }

    /// <summary>
    /// The first property in the chain. Used for direct claim lookup on the candidate entity.
    /// </summary>
    public string RootProperty => _segments[0];

    /// <summary>
    /// Whether this is a chained path (more than one segment).
    /// </summary>
    public bool IsChained => _segments.Length > 1;

    /// <summary>
    /// The remaining segments after the root (e.g., for "P131/P17", returns ["P17"]).
    /// </summary>
    public ReadOnlySpan<string> TailSegments => _segments.AsSpan(1);

    /// <summary>
    /// Segment property IDs in order (e.g. ["P131", "P17"]).
    /// </summary>
    public IReadOnlyList<string> Segments => _segments;

    /// <summary>
    /// Resolves claim values for a property path on an entity.
    /// For simple paths, returns values directly. For chained paths, resolves intermediate
    /// entity references through <paramref name="lookup"/>, which memoizes them per request.
    /// </summary>
    public async Task<List<ResolvedPropertyValue>> ResolveAsync(
        WikidataEntity entity,
        EntityLookup lookup,
        CancellationToken cancellationToken)
    {
        var values = GetPropertyValues(entity, _segments[0]);

        for (var i = 1; i < _segments.Length && values.Count > 0; i++)
        {
            var ids = ExtractEntityIds(values);
            if (ids.Count == 0)
                return [];

            var next = new List<ResolvedPropertyValue>();
            foreach (var intermediate in await lookup.GetAsync(ids, cancellationToken).ConfigureAwait(false))
                next.AddRange(GetPropertyValues(intermediate, _segments[i]));
            values = next;
        }

        return values;
    }

    /// <summary>
    /// Fetches every intermediate layer of this path for all <paramref name="candidates"/> at once,
    /// so per-candidate <see cref="ResolveAsync"/> calls hit the memo instead of the API.
    /// When <paramref name="includeTerminalItems"/> is true, item values at the end of the path are
    /// fetched too (their labels are needed to score name-valued constraints).
    /// </summary>
    public async Task PrefetchAsync(
        IEnumerable<WikidataEntity> candidates,
        EntityLookup lookup,
        bool includeTerminalItems,
        CancellationToken cancellationToken)
    {
        var current = candidates.ToList();
        for (var i = 0; i < _segments.Length && current.Count > 0; i++)
        {
            var values = current.SelectMany(entity => GetPropertyValues(entity, _segments[i])).ToList();
            var isLast = i == _segments.Length - 1;
            if (isLast && (!includeTerminalItems || !values.Any(v => v.DataType == "wikibase-item")))
                return;

            var ids = ExtractEntityIds(values);
            if (ids.Count == 0)
                return;

            current = await lookup.GetAsync(ids, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static List<ResolvedPropertyValue> GetPropertyValues(WikidataEntity entity, string propertyId)
    {
        if (entity.Claims?.TryGetValue(propertyId, out var claims) != true || claims!.Count == 0)
            return [];

        var validClaims = claims
            .Where(c => c.Rank != "deprecated" && c.MainSnak?.SnakType == "value" && c.MainSnak.DataValue != null)
            .ToList();

        var preferred = validClaims.Where(c => c.Rank == "preferred").ToList();
        var source = preferred.Count > 0 ? preferred : validClaims.Where(c => c.Rank == "normal").ToList();

        return source
            .Select(c => new ResolvedPropertyValue(c.MainSnak!.DataValue!, c.MainSnak.DataType))
            .ToList();
    }

    internal static List<string> ExtractEntityIds(IEnumerable<ResolvedPropertyValue> values)
    {
        var ids = new List<string>();
        foreach (var value in values)
        {
            if (value.DataValue.Value is JsonElement element && element.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString();
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }
        }

        return ids;
    }
}

internal readonly record struct ResolvedPropertyValue(DataValue DataValue, string? DataType);
