using System.Diagnostics;
using System.Runtime.CompilerServices;
using Tuvima.Wikidata.Internal;
using static Tuvima.Wikidata.Internal.BridgeEntityFacts;

namespace Tuvima.Wikidata.Services;

/// <summary>
/// High-level identity resolver for provider bridge IDs, canonical work rollups,
/// relationship extraction, and explainable Wikidata candidates.
/// Obtained via <see cref="WikidataReconciler.Bridge"/>.
/// </summary>
public sealed class BridgeResolutionService
{

    private readonly ReconcilerContext _ctx;
    private readonly ReconciliationService _reconcile;
    private readonly BridgeCandidateScorer _candidateScorer;

    internal BridgeResolutionService(ReconcilerContext ctx, ReconciliationService reconcile)
    {
        _ctx = ctx;
        _reconcile = reconcile;
        _candidateScorer = new BridgeCandidateScorer(ctx.Options.TypePropertyId);
    }

    /// <summary>
    /// Resolves a single bridge request.
    /// </summary>
    public async Task<BridgeResolutionResult> ResolveAsync(
        BridgeResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var results = await ResolveBatchAsync([request], cancellationToken).ConfigureAwait(false);
        return results.TryGetValue(request.CorrelationKey, out var result)
            ? result
            : BuildFailure(
                request.CorrelationKey,
                BridgeResolutionStatus.NotFound,
                WikidataFailureKind.NotFound,
                "No bridge resolution result was produced.",
                new BridgeDiagnosticsBuilder(),
                TimeSpan.Zero);
    }

    /// <summary>
    /// Resolves many bridge requests. External ID lookups are grouped by Wikidata property
    /// and normalized value so duplicate bridge IDs share one provider call.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, BridgeResolutionResult>> ResolveBatchAsync(
        IReadOnlyList<BridgeResolutionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var results = new Dictionary<string, BridgeResolutionResult>(StringComparer.Ordinal);
        await foreach (var result in ResolveBatchStreamAsync(requests, cancellationToken).ConfigureAwait(false))
            results[result.CorrelationKey] = result;

        return results;
    }

    /// <summary>
    /// Resolves many bridge requests and yields one result per correlation key as soon
    /// as the batched lookup/entity-prefetch phase has enough data to score that item.
    /// </summary>
    public async IAsyncEnumerable<BridgeResolutionResult> ResolveBatchStreamAsync(
        IReadOnlyList<BridgeResolutionRequest> requests,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
            yield break;

        var stopwatch = Stopwatch.StartNew();
        var before = _ctx.Diagnostics.GetSnapshot();
        var normalizedByKey = new Dictionary<string, IReadOnlyList<ResolvedBridgeIdentifier>>(StringComparer.Ordinal);
        var diagnosticsByKey = new Dictionary<string, BridgeDiagnosticsBuilder>(StringComparer.Ordinal);

        foreach (var request in requests)
        {
            var diagnostics = new BridgeDiagnosticsBuilder();
            diagnosticsByKey[request.CorrelationKey] = diagnostics;

            var identifiers = BridgeIdCatalog.Normalize(request);
            normalizedByKey[request.CorrelationKey] = identifiers;

            foreach (var identifier in identifiers)
                diagnostics.AttemptedStrategies.Add($"bridge:{identifier.NormalizedKey}:{identifier.PropertyId}");

            if (!string.IsNullOrWhiteSpace(request.Title))
                diagnostics.AttemptedStrategies.Add("text:fallback");
        }

        var distinctLookups = normalizedByKey.Values
            .SelectMany(x => x)
            .GroupBy(x => x.LookupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var completedItems = 0;
        var completedWorkUnits = 0;
        var totalWorkUnits = Math.Max(1, distinctLookups.Count + requests.Count);
        ReportProgress(
            WikidataProgressPhases.Planned,
            correlationKey: null,
            completedItems,
            requests.Count,
            completedWorkUnits,
            totalWorkUnits,
            stopwatch,
            $"Planned {requests.Count} bridge resolution item(s) with {distinctLookups.Count} distinct lookup(s).");

        var lookupResults = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var lookupFailures = new Dictionary<string, WikidataProviderException>(StringComparer.OrdinalIgnoreCase);

        foreach (var lookup in distinctLookups)
        {
            try
            {
                ReportProgress(
                    WikidataProgressPhases.ExternalIdLookup,
                    correlationKey: null,
                    completedItems,
                    requests.Count,
                    completedWorkUnits,
                    totalWorkUnits,
                    stopwatch,
                    $"Looking up {lookup.PropertyId}:{lookup.NormalizedValue}.");

                var qids = await _ctx.SearchClient
                    .SearchByExternalIdAsync(lookup.PropertyId, lookup.NormalizedValue, 20, cancellationToken)
                    .ConfigureAwait(false);
                lookupResults[lookup.LookupKey] = qids;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WikidataProviderException ex)
            {
                lookupFailures[lookup.LookupKey] = ex;
            }
            finally
            {
                completedWorkUnits++;
                ReportProgress(
                    WikidataProgressPhases.ExternalIdLookup,
                    correlationKey: null,
                    completedItems,
                    requests.Count,
                    completedWorkUnits,
                    totalWorkUnits,
                    stopwatch,
                    "Completed a bridge lookup.");
            }
        }

        var allQids = lookupResults.Values
            .SelectMany(qids => qids)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        totalWorkUnits = Math.Max(totalWorkUnits, distinctLookups.Count + allQids.Count + requests.Count);
        foreach (var diagnostics in diagnosticsByKey.Values)
        {
            diagnostics.DistinctLookupCount = distinctLookups.Count;
            diagnostics.FetchedEntityCount = allQids.Count;
        }

        ReportProgress(
            WikidataProgressPhases.EntityFetch,
            correlationKey: null,
            completedItems,
            requests.Count,
            completedWorkUnits,
            totalWorkUnits,
            stopwatch,
            $"Fetching {allQids.Count} candidate entity/entities.");

        var candidateEntitiesByLanguage = await FetchEntitiesByRequestLanguageAsync(
            allQids,
            requests,
            cancellationToken).ConfigureAwait(false);

        completedWorkUnits += allQids.Count;
        ReportProgress(
            WikidataProgressPhases.EntityFetch,
            correlationKey: null,
            completedItems,
            requests.Count,
            completedWorkUnits,
            totalWorkUnits,
            stopwatch,
            "Candidate entity fetch complete.");

        foreach (var request in requests)
        {
            var diagnostics = diagnosticsByKey[request.CorrelationKey];
            var identifiers = normalizedByKey[request.CorrelationKey];
            var language = request.Language ?? _ctx.Options.Language;
            var entities = candidateEntitiesByLanguage.TryGetValue(language, out var byQid)
                ? byQid
                : new Dictionary<string, WikidataEntityInfo>(StringComparer.OrdinalIgnoreCase);

            BridgeResolutionResult resolved;
            try
            {
                ReportProgress(
                    WikidataProgressPhases.Rollup,
                    request.CorrelationKey,
                    completedItems,
                    requests.Count,
                    completedWorkUnits,
                    totalWorkUnits,
                    stopwatch,
                    "Scoring candidates and resolving rollup.");

                resolved = await ResolveOneAsync(
                    request,
                    identifiers,
                    lookupResults,
                    lookupFailures,
                    entities,
                    diagnostics,
                    stopwatch,
                    before,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WikidataProviderException ex)
            {
                resolved = BuildFailure(
                    request.CorrelationKey,
                    BridgeResolutionStatus.Failed,
                    ex.Kind,
                    ex.Message,
                    diagnostics,
                    stopwatch.Elapsed,
                    before);
            }

            completedItems++;
            completedWorkUnits++;
            ReportProgress(
                resolved.Found ? WikidataProgressPhases.Completed : WikidataProgressPhases.Failed,
                request.CorrelationKey,
                completedItems,
                requests.Count,
                completedWorkUnits,
                totalWorkUnits,
                stopwatch,
                resolved.Found ? "Bridge resolution completed." : resolved.FailureMessage,
                resolved.FailureKind);

            yield return resolved;
        }
    }

    private async Task<BridgeResolutionResult> ResolveOneAsync(
        BridgeResolutionRequest request,
        IReadOnlyList<ResolvedBridgeIdentifier> identifiers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> lookupResults,
        IReadOnlyDictionary<string, WikidataProviderException> lookupFailures,
        IReadOnlyDictionary<string, WikidataEntityInfo> prefetchedEntities,
        BridgeDiagnosticsBuilder diagnostics,
        Stopwatch stopwatch,
        WikidataDiagnosticsSnapshot before,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CorrelationKey))
        {
            return BuildFailure(
                request.CorrelationKey ?? "",
                BridgeResolutionStatus.InvalidRequest,
                WikidataFailureKind.MalformedResponse,
                "BridgeResolutionRequest.CorrelationKey is required.",
                diagnostics,
                stopwatch.Elapsed,
                before);
        }

        if (identifiers.Count == 0 && string.IsNullOrWhiteSpace(request.Title))
        {
            return BuildFailure(
                request.CorrelationKey,
                BridgeResolutionStatus.InvalidRequest,
                WikidataFailureKind.NotFound,
                "No recognized bridge IDs or title hint were supplied.",
                diagnostics,
                stopwatch.Elapsed,
                before);
        }

        var language = request.Language ?? _ctx.Options.Language;
        var hintLabels = await FetchHintLabelsAsync(
            request,
            GetCandidateEntities(identifiers, lookupResults, prefetchedEntities),
            language,
            cancellationToken).ConfigureAwait(false);

        var bridgeCandidates = _candidateScorer.BuildBridgeCandidates(
            request,
            identifiers,
            lookupResults,
            prefetchedEntities,
            diagnostics,
            hintLabels);

        if (bridgeCandidates.Count > 0)
        {
            diagnostics.CompletedPhase = WikidataProgressPhases.Rollup;
            return await BuildResolvedResultAsync(
                request,
                bridgeCandidates,
                BridgeResolutionStrategy.BridgeId,
                diagnostics,
                stopwatch.Elapsed,
                before,
                cancellationToken).ConfigureAwait(false);
        }

        var failedLookup = identifiers
            .Select(i => i.LookupKey)
            .Where(lookupFailures.ContainsKey)
            .Select(key => lookupFailures[key])
            .FirstOrDefault();

        if (failedLookup is not null && string.IsNullOrWhiteSpace(request.Title))
        {
            return BuildFailure(
                request.CorrelationKey,
                BridgeResolutionStatus.Failed,
                failedLookup.Kind,
                failedLookup.Message,
                diagnostics,
                stopwatch.Elapsed,
                before);
        }

        ReportProgress(
            WikidataProgressPhases.TextFallback,
            request.CorrelationKey,
            completedItems: 0,
            totalItems: 0,
            completedWorkUnits: 0,
            totalWorkUnits: 0,
            stopwatch,
            "Trying text fallback.");

        var fallback = await ResolveByTextFallbackAsync(
            request,
            diagnostics,
            cancellationToken).ConfigureAwait(false);

        if (fallback.Count > 0)
        {
            diagnostics.CompletedPhase = WikidataProgressPhases.TextFallback;
            return await BuildResolvedResultAsync(
                request,
                fallback,
                BridgeResolutionStrategy.TextSearch,
                diagnostics,
                stopwatch.Elapsed,
                before,
                cancellationToken).ConfigureAwait(false);
        }

        if (failedLookup is not null)
        {
            diagnostics.CompletedPhase = WikidataProgressPhases.Failed;
            return BuildFailure(
                request.CorrelationKey,
                BridgeResolutionStatus.Failed,
                failedLookup.Kind,
                failedLookup.Message,
                diagnostics,
                stopwatch.Elapsed,
                before);
        }

        diagnostics.CompletedPhase = WikidataProgressPhases.Failed;
        return BuildFailure(
            request.CorrelationKey,
            BridgeResolutionStatus.NotFound,
            WikidataFailureKind.NotFound,
            "No Wikidata candidate matched the supplied bridge IDs or title hints.",
            diagnostics,
            stopwatch.Elapsed,
            before);
    }

    private async Task<Dictionary<string, Dictionary<string, WikidataEntityInfo>>> FetchEntitiesByRequestLanguageAsync(
        IReadOnlyList<string> qids,
        IReadOnlyList<BridgeResolutionRequest> requests,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Dictionary<string, WikidataEntityInfo>>(StringComparer.OrdinalIgnoreCase);
        if (qids.Count == 0)
            return result;

        var languages = requests
            .Select(r => r.Language ?? _ctx.Options.Language)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var language in languages)
        {
            var fetched = await _ctx.EntityFetcher.FetchEntitiesAsync(qids, language, cancellationToken)
                .ConfigureAwait(false);
            result[language] = fetched.ToDictionary(
                kvp => kvp.Key,
                kvp => EntityMapper.MapEntity(kvp.Value, language),
                StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    private static IReadOnlyList<WikidataEntityInfo> GetCandidateEntities(
        IReadOnlyList<ResolvedBridgeIdentifier> identifiers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> lookupResults,
        IReadOnlyDictionary<string, WikidataEntityInfo> entities)
    {
        var qids = identifiers
            .Where(identifier => lookupResults.ContainsKey(identifier.LookupKey))
            .SelectMany(identifier => lookupResults[identifier.LookupKey])
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var result = new List<WikidataEntityInfo>();
        foreach (var qid in qids)
        {
            if (entities.TryGetValue(qid, out var entity))
                result.Add(entity);
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<string, string?>> FetchHintLabelsAsync(
        BridgeResolutionRequest request,
        IEnumerable<WikidataEntityInfo> entities,
        string language,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Creator) && string.IsNullOrWhiteSpace(request.SeriesTitle))
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var qids = entities
            .SelectMany(entity => CreatorPropertyIds.Concat(SeriesPropertyIds)
                .SelectMany(propertyId => GetEntityIds(entity, propertyId)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return qids.Count == 0
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : await FetchLabelsAsync(qids, language, cancellationToken).ConfigureAwait(false);
    }

    private static List<PropertyConstraint> BuildTextFallbackConstraints(BridgeResolutionRequest request)
    {
        var constraints = new List<PropertyConstraint>();
        if (request.MediaKind is not (BridgeMediaKind.Book or BridgeMediaKind.Audiobook))
            return constraints;

        if (!string.IsNullOrWhiteSpace(request.Creator))
            constraints.Add(new PropertyConstraint("P50", [request.Creator.Trim()]));
        if (!string.IsNullOrWhiteSpace(request.SeriesTitle))
            constraints.Add(new PropertyConstraint("P179", [request.SeriesTitle.Trim()]));
        return constraints;
    }

    private async Task<IReadOnlyList<BridgeCandidate>> ResolveByTextFallbackAsync(
        BridgeResolutionRequest request,
        BridgeDiagnosticsBuilder diagnostics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return [];

        var language = request.Language ?? _ctx.Options.Language;
        var types = BridgeCandidateScorer.GetMediaTypeHints(request.MediaKind);

        try
        {
            // A short, common title ("Drive") matches many items with the same label, and the search order between equals is only the
            // item number, so the creator and series the caller knows are sent as scoring constraints: the right item then ranks first
            // and a wider window is read so it is not cut off. Subclasses of the media types count (a novella is a written work).
            var constraints = BuildTextFallbackConstraints(request);
            var matches = await _reconcile.ReconcileAsync(new ReconciliationRequest
            {
                Query = request.Title,
                Types = types.Count > 0 ? types : null,
                TypeHierarchyDepth = types.Count > 0 ? 2 : null,
                Properties = constraints.Count > 0 ? constraints : null,
                Language = language,
                Limit = constraints.Count > 0 ? 15 : 5
            }, cancellationToken).ConfigureAwait(false);

            var qids = matches.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (qids.Count == 0)
                return [];

            var fetched = await _ctx.EntityFetcher.FetchEntitiesAsync(qids, language, cancellationToken)
                .ConfigureAwait(false);
            var mapped = fetched.ToDictionary(
                kvp => kvp.Key,
                kvp => EntityMapper.MapEntity(kvp.Value, language),
                StringComparer.OrdinalIgnoreCase);
            var hintLabels = await FetchHintLabelsAsync(
                request,
                mapped.Values,
                language,
                cancellationToken).ConfigureAwait(false);

            var candidates = new List<BridgeCandidate>();
            foreach (var match in matches)
            {
                if (!mapped.TryGetValue(match.Id, out var entity))
                    continue;

                candidates.Add(_candidateScorer.BuildTextCandidate(request, match, entity, hintLabels));
            }

            return BridgeCandidateScorer.SortCandidates(candidates);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WikidataProviderException ex)
        {
            diagnostics.Warnings.Add($"text-fallback-provider-failure:{ex.Kind}");
            return [];
        }
    }

    private async Task<BridgeResolutionResult> BuildResolvedResultAsync(
        BridgeResolutionRequest request,
        IReadOnlyList<BridgeCandidate> candidates,
        BridgeResolutionStrategy strategy,
        BridgeDiagnosticsBuilder diagnostics,
        TimeSpan providerLatency,
        WikidataDiagnosticsSnapshot before,
        CancellationToken cancellationToken)
    {
        diagnostics.CompletedPhase = WikidataProgressPhases.Completed;
        var selected = candidates[0];
        if (candidates.Count > 1 && Math.Abs(candidates[0].Confidence - candidates[1].Confidence) < 0.02)
            diagnostics.Warnings.Add("candidate.ambiguous");

        var language = request.Language ?? _ctx.Options.Language;
        var entity = await FetchPublicEntityAsync(selected.Qid, language, cancellationToken).ConfigureAwait(false);
        var rollup = entity is null
            ? new CanonicalRollup
            {
                ResolvedEntityQid = selected.Qid,
                CanonicalWorkQid = selected.Qid,
                IsRollup = false
            }
            : BridgeCanonicalRollup.BuildRollup(request, entity);

        var relatedQids = entity is null ? [] : BridgeRelationshipExtractor.GetRelationshipQids(entity);
        var relatedEntities = relatedQids.Count == 0
            ? new Dictionary<string, WikidataEntityInfo>(StringComparer.OrdinalIgnoreCase)
            : await FetchPublicEntitiesAsync(relatedQids, language, cancellationToken).ConfigureAwait(false);
        var labels = relatedQids.ToDictionary(
            qid => qid,
            qid => relatedEntities.TryGetValue(qid, out var related) ? related.Label : null,
            StringComparer.OrdinalIgnoreCase);

        var series = entity is null ? [] : BridgeRelationshipExtractor.ExtractSeries(entity, labels, relatedEntities);
        var relationships = entity is null ? [] : BridgeRelationshipExtractor.ExtractRelationships(entity, labels);

        return new BridgeResolutionResult
        {
            CorrelationKey = request.CorrelationKey,
            Status = BridgeResolutionStatus.Resolved,
            MatchedBy = strategy,
            SelectedCandidate = selected,
            Candidates = candidates,
            Rollup = rollup,
            Series = series,
            Relationships = relationships,
            Diagnostics = diagnostics.Build(providerLatency, before, _ctx.Diagnostics.GetSnapshot())
        };
    }

    private void ReportProgress(
        string phase,
        string? correlationKey,
        int completedItems,
        int totalItems,
        int completedWorkUnits,
        int totalWorkUnits,
        Stopwatch stopwatch,
        string? message = null,
        WikidataFailureKind? failureKind = null)
    {
        _ctx.Options.ProgressReporter?.Invoke(new WikidataProgressEvent(
            WikidataProgressOperations.BridgeResolution,
            phase,
            correlationKey,
            completedItems,
            totalItems,
            completedWorkUnits,
            totalWorkUnits,
            stopwatch.Elapsed,
            message,
            failureKind));
    }

    private async Task<WikidataEntityInfo?> FetchPublicEntityAsync(
        string qid,
        string language,
        CancellationToken cancellationToken)
    {
        var fetched = await _ctx.EntityFetcher.FetchEntitiesAsync([qid], language, cancellationToken)
            .ConfigureAwait(false);
        return fetched.TryGetValue(qid, out var entity)
            ? EntityMapper.MapEntity(entity, language)
            : null;
    }

    private async Task<Dictionary<string, WikidataEntityInfo>> FetchPublicEntitiesAsync(
        IReadOnlyList<string> qids,
        string language,
        CancellationToken cancellationToken)
    {
        if (qids.Count == 0)
            return new Dictionary<string, WikidataEntityInfo>(StringComparer.OrdinalIgnoreCase);

        var fetched = await _ctx.EntityFetcher.FetchEntitiesAsync(qids, language, cancellationToken)
            .ConfigureAwait(false);
        return fetched.ToDictionary(
            kvp => kvp.Key,
            kvp => EntityMapper.MapEntity(kvp.Value, language),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<string, string?>> FetchLabelsAsync(
        IReadOnlyList<string> qids,
        string language,
        CancellationToken cancellationToken)
    {
        var fetched = await _ctx.EntityFetcher.FetchLabelsOnlyAsync(qids, language, cancellationToken)
            .ConfigureAwait(false);

        return fetched.ToDictionary(
            kvp => kvp.Key,
            kvp =>
            {
                LanguageFallback.TryGetValue(kvp.Value.Labels, language, out var label);
                return string.IsNullOrWhiteSpace(label) ? null : label;
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static BridgeResolutionResult BuildFailure(
        string correlationKey,
        BridgeResolutionStatus status,
        WikidataFailureKind failureKind,
        string message,
        BridgeDiagnosticsBuilder diagnostics,
        TimeSpan providerLatency,
        WikidataDiagnosticsSnapshot? before = null)
    {
        var after = before ?? new WikidataDiagnosticsSnapshot();
        diagnostics.CompletedPhase = WikidataProgressPhases.Failed;
        return new BridgeResolutionResult
        {
            CorrelationKey = correlationKey,
            Status = status,
            FailureKind = failureKind,
            FailureMessage = message,
            Diagnostics = before is null
                ? diagnostics.Build(providerLatency, after, after)
                : diagnostics.Build(providerLatency, before, after)
        };
    }

}
