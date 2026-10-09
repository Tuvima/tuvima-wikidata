using Tuvima.Wikidata.Internal.Json;

namespace Tuvima.Wikidata.Internal;

/// <summary>
/// Computes reconciliation scores using the weighted-average algorithm from openrefine-wikibase.
/// Label weight = 1.0, each property weight = 0.4.
/// </summary>
internal sealed class ReconciliationScorer
{
    private readonly WikidataReconcilerOptions _options;

    public ReconciliationScorer(WikidataReconcilerOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Scores a candidate entity against a reconciliation request.
    /// Returns both the overall score and a detailed breakdown.
    /// </summary>
    public async Task<ScoringResult> ScoreAsync(
        string query,
        WikidataEntity entity,
        string language,
        IReadOnlyList<PropertyConstraint>? properties,
        EntityLookup lookup,
        CancellationToken cancellationToken,
        bool diacriticInsensitive = false)
    {
        // Label matching: fuzzy match query against all labels + aliases across ALL languages, take max
        var allLabels = _options.IncludeSitelinkLabels
            ? WikidataEntityFetcher.GetAllLabelsWithSitelinks(entity)
            : WikidataEntityFetcher.GetAllLabelsAllLanguages(entity);
        var labelScore = 0;
        string? matchedLabel = null;

        foreach (var label in allLabels)
        {
            var score = FuzzyMatcher.TokenSortRatio(query, label, diacriticInsensitive);
            if (score > labelScore)
            {
                labelScore = score;
                matchedLabel = label;
            }
        }

        if (allLabels.Count == 0)
            labelScore = 0;

        var propertyWeight = _options.PropertyWeight;
        var totalWeight = 1.0;
        var weightedSum = labelScore * 1.0;
        var propertyScores = new Dictionary<string, double>();

        // Property matching
        if (properties is { Count: > 0 })
        {
            foreach (var prop in properties)
            {
                var propScore = await ScorePropertyAsync(entity, prop, lookup, diacriticInsensitive, cancellationToken)
                    .ConfigureAwait(false);
                propertyScores[prop.PropertyId] = propScore;
                weightedSum += propScore * propertyWeight;
                totalWeight += propertyWeight;
            }
        }

        var weightedScore = weightedSum / totalWeight;

        // Unique ID shortcut: if any unique-ID property scores 100, return 100
        if (properties is { Count: > 0 })
        {
            foreach (var prop in properties)
            {
                if (_options.UniqueIdProperties.Contains(prop.PropertyId) &&
                    propertyScores.TryGetValue(prop.PropertyId, out var ps) && ps >= 100)
                {
                    return new ScoringResult
                    {
                        Score = 100,
                        LabelScore = labelScore,
                        MatchedLabel = matchedLabel,
                        PropertyScores = propertyScores,
                        WeightedScore = weightedScore,
                        UniqueIdMatch = true
                    };
                }
            }
        }

        return new ScoringResult
        {
            Score = weightedScore,
            LabelScore = labelScore,
            MatchedLabel = matchedLabel,
            PropertyScores = propertyScores,
            WeightedScore = weightedScore
        };
    }

    /// <summary>
    /// Scores a single property constraint against an entity's claims.
    /// For single-value constraints, returns the best match across all claim values.
    /// For multi-value constraints, returns the average of the best match for each constraint value.
    /// </summary>
    private static async Task<int> ScorePropertyAsync(
        WikidataEntity entity,
        PropertyConstraint constraint,
        EntityLookup lookup,
        bool diacriticInsensitive,
        CancellationToken cancellationToken)
    {
        var path = new PropertyPath(constraint.PropertyId);
        var claimValues = await path.ResolveAsync(entity, lookup, cancellationToken)
            .ConfigureAwait(false);

        if (claimValues.Count == 0)
            return 0;

        if (constraint.Values is not { Count: > 0 } effectiveValues)
            return 0;

        // For each constraint value, find the best match across all entity claim values.
        // The property score is the average of these best-match scores.
        var totalScore = 0.0;
        foreach (var constraintValue in effectiveValues)
        {
            var bestScore = 0;
            foreach (var claimValue in claimValues)
            {
                var score = claimValue.DataType == "wikibase-item" && !PropertyMatcher.IsQid(constraintValue)
                    ? await MatchItemLabelAsync(constraintValue, claimValue, lookup, diacriticInsensitive, cancellationToken)
                        .ConfigureAwait(false)
                    : PropertyMatcher.Match(constraintValue, claimValue.DataValue, claimValue.DataType);
                if (score > bestScore)
                    bestScore = score;
            }
            totalScore += bestScore;
        }

        return (int)Math.Round(totalScore / effectiveValues.Count);
    }

    /// <summary>
    /// Fuzzy-matches a name (e.g. "Neil Gaiman") against the labels and aliases of the item a claim
    /// points to. Mirrors openrefine-wikibase, where item-valued properties accept names or QIDs.
    /// </summary>
    private static async Task<int> MatchItemLabelAsync(
        string constraintValue,
        ResolvedPropertyValue claimValue,
        EntityLookup lookup,
        bool diacriticInsensitive,
        CancellationToken cancellationToken)
    {
        var ids = PropertyPath.ExtractEntityIds([claimValue]);
        if (ids.Count == 0)
            return 0;

        var best = 0;
        foreach (var item in await lookup.GetAsync(ids, cancellationToken).ConfigureAwait(false))
        {
            foreach (var label in WikidataEntityFetcher.GetAllLabelsAllLanguages(item))
                best = Math.Max(best, FuzzyMatcher.TokenSortRatio(constraintValue, label, diacriticInsensitive));
        }
        return best;
    }

    /// <summary>
    /// Fetches, once for all candidates, every entity that property scoring will need:
    /// chained-path intermediates and, for name-valued constraints, the referenced items.
    /// </summary>
    public static async Task PrefetchAsync(
        IReadOnlyCollection<WikidataEntity> candidates,
        IReadOnlyList<PropertyConstraint>? properties,
        EntityLookup lookup,
        CancellationToken cancellationToken)
    {
        if (properties is not { Count: > 0 } || candidates.Count == 0)
            return;

        foreach (var constraint in properties)
        {
            if (constraint.Values is not { Count: > 0 })
                continue;

            var includeTerminalItems = constraint.Values.Any(v => !PropertyMatcher.IsQid(v));
            await new PropertyPath(constraint.PropertyId)
                .PrefetchAsync(candidates, lookup, includeTerminalItems, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Determines auto-match status for sorted results.
    /// </summary>
    public bool IsAutoMatch(double score, double? secondBestScore, int numProperties)
    {
        var threshold = _options.AutoMatchThreshold - 5.0 * numProperties;
        if (score <= threshold)
            return false;

        if (secondBestScore.HasValue && score <= secondBestScore.Value + _options.AutoMatchScoreGap)
            return false;

        return true;
    }
}

internal sealed record ScoringResult
{
    public double Score { get; init; }
    public double LabelScore { get; init; }
    public string? MatchedLabel { get; init; }
    public Dictionary<string, double> PropertyScores { get; init; } = new();
    public double WeightedScore { get; init; }
    public bool UniqueIdMatch { get; init; }
}
