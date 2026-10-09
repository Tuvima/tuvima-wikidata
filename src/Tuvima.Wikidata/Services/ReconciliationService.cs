using System.Runtime.CompilerServices;
using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Services;

/// <summary>
/// Reconciliation and suggest (autocomplete) operations.
/// Obtained via <see cref="WikidataReconciler.Reconcile"/>.
/// </summary>
public sealed class ReconciliationService
{
    private readonly ReconcilerContext _ctx;

    internal ReconciliationService(ReconcilerContext ctx) => _ctx = ctx;

    /// <summary>
    /// Reconciles a text query against Wikidata.
    /// </summary>
    public Task<IReadOnlyList<ReconciliationResult>> ReconcileAsync(
        string query, CancellationToken cancellationToken = default)
        => ReconcileAsync(new ReconciliationRequest { Query = query }, cancellationToken);

    /// <summary>
    /// Reconciles a text query with a type constraint.
    /// </summary>
    public Task<IReadOnlyList<ReconciliationResult>> ReconcileAsync(
        string query, string type, CancellationToken cancellationToken = default)
        => ReconcileAsync(new ReconciliationRequest { Query = query, Types = [type] }, cancellationToken);

    /// <summary>
    /// Reconciles a query with full options (types, properties, language, etc.).
    /// </summary>
    public async Task<IReadOnlyList<ReconciliationResult>> ReconcileAsync(
        ReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);

        var options = _ctx.Options;
        var language = request.Language ?? options.Language;
        var limit = request.Limit > 0 ? request.Limit : 5;

        // Apply query cleaners (cleaned text for search, original for scoring)
        var searchQuery = request.Query;
        if (request.Cleaners is { Count: > 0 })
        {
            foreach (var cleaner in request.Cleaners)
                searchQuery = cleaner(searchQuery);

            if (string.IsNullOrWhiteSpace(searchQuery))
                searchQuery = request.Query;
        }

        var effectiveTypes = request.Types is { Count: > 0 } ? request.Types : null;
        var hasTypeConstraint = effectiveTypes is { Count: > 0 };

        // Resolve per-request subclass resolver
        var subclassResolver = _ctx.SubclassResolver;
        if (request.TypeHierarchyDepth is > 0 && subclassResolver == null)
        {
            // Depth is passed per call, so the shared resolver keeps its P279 cache across requests.
            subclassResolver = _ctx.OnDemandSubclassResolver;
        }

        if (TryNormalizeQid(searchQuery, out var exactQid))
        {
            return await ReconcileExactQidAsync(
                exactQid,
                request,
                language,
                effectiveTypes,
                hasTypeConstraint,
                subclassResolver,
                options,
                cancellationToken).ConfigureAwait(false);
        }

        // Step 1: Search for candidate entity IDs
        var useMultiLanguage = request.Languages is { Count: > 1 };
        var searchTasks = new List<Task<List<string>>>();

        if (useMultiLanguage)
        {
            searchTasks.Add(_ctx.SearchClient.SearchMultiLanguageAsync(searchQuery, request.Languages!,
                limit, request.DiacriticInsensitive, cancellationToken));
        }
        else
        {
            searchTasks.Add(_ctx.SearchClient.SearchAsync(searchQuery, language, limit,
                request.DiacriticInsensitive, cancellationToken));
        }

        if (hasTypeConstraint)
        {
            searchTasks.Add(_ctx.SearchClient.SearchWithTypeFilterAsync(
                searchQuery, language, effectiveTypes!, limit, cancellationToken));
        }

        await Task.WhenAll(searchTasks).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateIds = new List<string>();

        if (hasTypeConstraint)
        {
            foreach (var id in await searchTasks[^1].ConfigureAwait(false))
            {
                if (seen.Add(id))
                    candidateIds.Add(id);
            }
        }

        foreach (var id in await searchTasks[0].ConfigureAwait(false))
        {
            if (seen.Add(id))
                candidateIds.Add(id);
        }

        if (candidateIds.Count == 0)
            return [];

        // Step 2: Fetch entity data
        var entities = await _ctx.EntityFetcher.FetchEntitiesAllLanguagesAsync(
            candidateIds, options.IncludeSitelinkLabels, cancellationToken).ConfigureAwait(false);

        // Step 3: Score and filter candidates. Referenced entities (chained-path intermediates and
        // items named by text constraints) are fetched once for all candidates, not per candidate.
        var lookup = new EntityLookup(_ctx.EntityFetcher, language);
        await ReconciliationScorer.PrefetchAsync(entities.Values, request.Properties, lookup, cancellationToken)
            .ConfigureAwait(false);

        var scored = new List<(string Id, Internal.Json.WikidataEntity Entity, ScoringResult Scoring, List<string> Types, TypeMatchResult TypeResult)>();

        foreach (var id in candidateIds)
        {
            if (!entities.TryGetValue(id, out var entity))
                continue;

            var typeResult = await _ctx.TypeChecker.CheckAsync(
                entity, effectiveTypes, request.ExcludeTypes,
                subclassResolver, language, cancellationToken, request.TypeHierarchyDepth).ConfigureAwait(false);

            if (typeResult == TypeMatchResult.Excluded || typeResult == TypeMatchResult.NotMatched)
                continue;

            var scoring = await _ctx.Scorer.ScoreAsync(
                request.Query,
                entity,
                language,
                request.Properties,
                lookup,
                cancellationToken,
                request.DiacriticInsensitive).ConfigureAwait(false);

            var finalScore = scoring.Score;
            if (typeResult == TypeMatchResult.NoType && hasTypeConstraint)
                finalScore /= 2.0;

            var types = WikidataEntityFetcher.GetTypeIds(entity, options.TypePropertyId);
            scored.Add((id, entity, scoring with { Score = finalScore }, types, typeResult));
        }

        scored.Sort((a, b) =>
        {
            var cmp = b.Scoring.Score.CompareTo(a.Scoring.Score);
            if (cmp != 0) return cmp;
            return CompareQids(a.Id, b.Id);
        });

        var numProperties = request.Properties?.Count ?? 0;
        var results = new List<ReconciliationResult>();

        for (var i = 0; i < Math.Min(scored.Count, limit); i++)
        {
            var (id, entity, scoring, types, typeResult) = scored[i];
            double? secondBest = i == 0 && scored.Count > 1 ? scored[1].Scoring.Score : null;

            LanguageFallback.TryGetValue(entity.Labels, language, out var label);
            LanguageFallback.TryGetValue(entity.Descriptions, language, out var description);

            var typePenaltyApplied = typeResult == TypeMatchResult.NoType && hasTypeConstraint;

            results.Add(new ReconciliationResult
            {
                Id = id,
                Name = string.IsNullOrEmpty(label) ? id : label,
                Description = string.IsNullOrEmpty(description) ? null : description,
                Score = Math.Round(scoring.Score, 2),
                Match = i == 0 && (scoring.UniqueIdMatch || _ctx.Scorer.IsAutoMatch(scoring.Score, secondBest, numProperties)),
                Types = types.Count > 0 ? types : null,
                MatchedLabel = scoring.MatchedLabel,
                Breakdown = new ScoreBreakdown
                {
                    LabelScore = scoring.LabelScore,
                    MatchedLabel = scoring.MatchedLabel,
                    PropertyScores = scoring.PropertyScores,
                    TypeMatched = !hasTypeConstraint ? null : typeResult == TypeMatchResult.Matched,
                    WeightedScore = Math.Round(scoring.WeightedScore, 2),
                    TypePenaltyApplied = typePenaltyApplied,
                    UniqueIdMatch = scoring.UniqueIdMatch
                }
            });
        }

        return results;
    }

    private async Task<IReadOnlyList<ReconciliationResult>> ReconcileExactQidAsync(
        string qid,
        ReconciliationRequest request,
        string language,
        IReadOnlyList<string>? effectiveTypes,
        bool hasTypeConstraint,
        SubclassResolver? subclassResolver,
        WikidataReconcilerOptions options,
        CancellationToken cancellationToken)
    {
        var entities = await _ctx.EntityFetcher.FetchEntitiesAllLanguagesAsync(
            [qid], options.IncludeSitelinkLabels, cancellationToken).ConfigureAwait(false);

        if (!entities.TryGetValue(qid, out var entity)
            || string.IsNullOrWhiteSpace(entity.Id)
            || !string.Equals(entity.Type, "item", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var typeResult = await _ctx.TypeChecker.CheckAsync(
            entity,
            effectiveTypes,
            request.ExcludeTypes,
            subclassResolver,
            language,
            cancellationToken,
            request.TypeHierarchyDepth).ConfigureAwait(false);

        if (typeResult == TypeMatchResult.Excluded || typeResult == TypeMatchResult.NotMatched)
            return [];

        LanguageFallback.TryGetValue(entity.Labels, language, out var label);
        LanguageFallback.TryGetValue(entity.Descriptions, language, out var description);

        var types = WikidataEntityFetcher.GetTypeIds(entity, options.TypePropertyId);
        var typePenaltyApplied = typeResult == TypeMatchResult.NoType && hasTypeConstraint;
        var score = typePenaltyApplied ? 50.0 : 100.0;

        return
        [
            new ReconciliationResult
            {
                Id = entity.Id,
                Name = string.IsNullOrEmpty(label) ? entity.Id : label,
                Description = string.IsNullOrEmpty(description) ? null : description,
                Score = score,
                Match = !typePenaltyApplied,
                Types = types.Count > 0 ? types : null,
                MatchedLabel = string.IsNullOrEmpty(label) ? entity.Id : label,
                Breakdown = new ScoreBreakdown
                {
                    LabelScore = 100,
                    MatchedLabel = string.IsNullOrEmpty(label) ? entity.Id : label,
                    PropertyScores = new Dictionary<string, double>(),
                    TypeMatched = !hasTypeConstraint ? null : typeResult == TypeMatchResult.Matched,
                    WeightedScore = score,
                    TypePenaltyApplied = typePenaltyApplied,
                    UniqueIdMatch = true
                }
            }
        ];
    }

    /// <summary>
    /// Reconciles multiple queries in parallel, respecting the configured concurrency limit.
    /// Results preserve input order. Identical requests in the batch run once and share a result.
    /// Failure or cancellation stops pending work.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyList<ReconciliationResult>>> ReconcileBatchAsync(
        IReadOnlyList<ReconciliationRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var groups = ReconciliationRequestGroups.Create(requests);
        var unique = await BoundedAsync.SelectAsync(groups.Representatives, _ctx.Options.MaxConcurrency,
            (request, token) => ReconcileAsync(request, token), cancellationToken).ConfigureAwait(false);

        var results = new IReadOnlyList<ReconciliationResult>[requests.Count];
        for (var i = 0; i < results.Length; i++)
            results[i] = unique[groups.GroupOf[i]];
        return results;
    }

    /// <summary>
    /// Reconciles multiple queries as a streaming async enumerable.
    /// Starts a bounded window of work and yields completed items with their input indices.
    /// Identical requests run once; each of their input indices is yielded when that run completes.
    /// Disposing the enumerator early cancels and observes outstanding work.
    /// </summary>
    public async IAsyncEnumerable<(int Index, IReadOnlyList<ReconciliationResult> Results)> ReconcileBatchStreamAsync(
        IReadOnlyList<ReconciliationRequest> requests, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var groups = ReconciliationRequestGroups.Create(requests);
        await foreach (var item in BoundedAsync.StreamAsync(groups.Representatives, _ctx.Options.MaxConcurrency,
            (request, token) => ReconcileAsync(request, token), cancellationToken).ConfigureAwait(false))
        {
            foreach (var index in groups.Members[item.Index])
                yield return (index, item.Result);
        }
    }

    /// <summary>
    /// Suggests Wikidata entities matching a text prefix.
    /// </summary>
    public async Task<IReadOnlyList<SuggestResult>> SuggestAsync(
        string prefix, int limit = 7, string? language = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var lang = language ?? _ctx.Options.Language;
        var searchResults = await _ctx.SearchClient.SuggestAsync(prefix, lang, limit, cancellationToken)
            .ConfigureAwait(false);

        return searchResults.Select(r => new SuggestResult
        {
            Id = r.Id,
            Name = r.Label ?? r.Id,
            Description = r.Description
        }).ToList();
    }

    /// <summary>
    /// Suggests Wikidata properties matching a text prefix.
    /// </summary>
    public async Task<IReadOnlyList<SuggestResult>> SuggestPropertiesAsync(
        string prefix, int limit = 7, string? language = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var lang = language ?? _ctx.Options.Language;
        var searchResults = await _ctx.SearchClient.SuggestPropertiesAsync(prefix, lang, limit, cancellationToken)
            .ConfigureAwait(false);

        return searchResults.Select(r => new SuggestResult
        {
            Id = r.Id,
            Name = r.Label ?? r.Id,
            Description = r.Description
        }).ToList();
    }

    /// <summary>
    /// Suggests Wikidata types/classes matching a text prefix.
    /// Results are restricted to classes (entities with a P279 "subclass of" statement) so that
    /// typing "novel" suggests the class rather than individual novels. If none of the matches
    /// is a class, the plain entity suggestions are returned instead.
    /// </summary>
    public async Task<IReadOnlyList<SuggestResult>> SuggestTypesAsync(
        string prefix, int limit = 7, string? language = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var lang = language ?? _ctx.Options.Language;
        var searchLimit = Math.Clamp(limit * 3, limit, 50);
        var searchResults = await _ctx.SearchClient.SuggestAsync(prefix, lang, searchLimit, cancellationToken)
            .ConfigureAwait(false);
        if (searchResults.Count == 0)
            return [];

        var claims = await _ctx.EntityFetcher.FetchClaimsOnlyAsync(
            searchResults.Select(r => r.Id).ToList(), cancellationToken).ConfigureAwait(false);

        var classes = searchResults
            .Where(r => claims.TryGetValue(r.Id, out var entity) &&
                        entity.Claims?.TryGetValue("P279", out var p279) == true && p279.Count > 0)
            .ToList();
        var chosen = classes.Count > 0 ? classes : searchResults;

        return chosen.Take(limit).Select(r => new SuggestResult
        {
            Id = r.Id,
            Name = r.Label ?? r.Id,
            Description = r.Description
        }).ToList();
    }

    private static int CompareQids(string a, string b)
    {
        if (a.Length > 1 && b.Length > 1 &&
            long.TryParse(a.AsSpan(1), out var numA) &&
            long.TryParse(b.AsSpan(1), out var numB))
        {
            return numA.CompareTo(numB);
        }

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeQid(string value, out string qid)
    {
        qid = "";
        value = value.Trim();

        if (value.Length < 2 || value[0] is not ('Q' or 'q') || value[1] == '0')
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            if (!char.IsDigit(value[i]))
                return false;
        }

        qid = string.Create(value.Length, value, static (buffer, source) =>
        {
            buffer[0] = 'Q';
            source.AsSpan(1).CopyTo(buffer[1..]);
        });
        return true;
    }
}
