using System.Collections.Frozen;
using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Services;

/// <summary>
/// Role-aware person search. Reconciles a raw name against Q5 (human) — plus optionally
/// Q215380 (musical group) and Q5741069 (musical ensemble) — with scoring weighted by
/// role-appropriate occupations, notable-work context, and year/companion hints.
/// Obtained via <see cref="WikidataReconciler.Persons"/>.
/// </summary>
public sealed class PersonsService
{
    private const string HumanType = "Q5";
    private const string MusicalGroupType = "Q215380";
    private const string MusicalEnsembleType = "Q5741069";
    private const int MaxBatchConcurrency = 4;

    // Maps each role to the P106 (occupation) QIDs that best match it.
    // When Role == Unknown, no occupation filter is applied.
    private static readonly FrozenDictionary<PersonRole, string[]> RoleOccupations =
        new Dictionary<PersonRole, string[]>
        {
            [PersonRole.Author] = ["Q36180", "Q4853732", "Q49757"],              // writer, author, poet
            [PersonRole.Narrator] = ["Q1622272", "Q10800557"],                     // voice actor, audiobook narrator
            [PersonRole.Director] = ["Q2526255", "Q2722764", "Q3282637"],          // film director, TV director, film producer
            [PersonRole.Actor] = ["Q33999", "Q10800557", "Q2405480"],              // actor, voice actor, voice actor
            [PersonRole.VoiceActor] = ["Q1622272", "Q10800557"],                   // voice actor, audiobook narrator
            [PersonRole.Composer] = ["Q36834", "Q486748", "Q639669"],              // composer, film composer, musician
            [PersonRole.Performer] = ["Q488205", "Q639669", "Q177220", "Q33999"],  // performing artist, musician, singer, actor
            [PersonRole.Artist] = ["Q483501", "Q639669", "Q1028181"],              // artist, musician, painter
            [PersonRole.Screenwriter] = ["Q28389", "Q36180"]                       // screenwriter, writer
        }.ToFrozenDictionary();

    // Roles whose default IncludeMusicalGroups is true.
    private static readonly FrozenSet<PersonRole> MusicalGroupDefaultRoles =
        new HashSet<PersonRole> { PersonRole.Performer, PersonRole.Artist }.ToFrozenSet();

    private readonly ReconcilerContext _ctx;
    private readonly ReconciliationService _reconcile;
    private readonly LabelsService _labels;

    internal PersonsService(ReconcilerContext ctx, ReconciliationService reconcile, LabelsService labels)
    {
        _ctx = ctx;
        _reconcile = reconcile;
        _labels = labels;
    }

    /// <summary>
    /// Searches for a person (or musical group) matching the supplied name and role.
    /// </summary>
    public async Task<PersonSearchResult> SearchAsync(
        PersonSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

        var language = request.Language ?? _ctx.Options.Language;
        var includeGroups = request.IncludeMusicalGroups
            ?? MusicalGroupDefaultRoles.Contains(request.Role);

        // Build the types filter for reconciliation.
        var types = new List<string> { HumanType };
        if (includeGroups)
        {
            types.Add(MusicalGroupType);
            types.Add(MusicalEnsembleType);
        }

        // Build property constraints from role and year hints. The P106 occupation constraint
        // is skipped when musical groups are included because groups don't carry P106 claims
        // and the constraint would unfairly penalise them during scoring.
        var constraints = BuildConstraints(request, includeGroups);

        var reconcileRequest = new ReconciliationRequest
        {
            Query = request.Name,
            Types = types,
            Properties = constraints.Count > 0 ? constraints : null,
            Language = language,
            Limit = 5,
            // People carry P31=Q5 (or a group type) directly, so the type-filtered search finds them without a second, unfiltered one.
            TypeFilteredSearchOnly = true
        };

        var matches = await _reconcile.ReconcileAsync(reconcileRequest, cancellationToken)
            .ConfigureAwait(false);

        if (matches.Count == 0)
        {
            return new PersonSearchResult { Found = false, Score = 0.0 };
        }

        // Optional companion-hint re-ranking: boost candidates whose P800 (notable work)
        // labels fuzzy-match any of the supplied companion names. One extra API round-trip
        // when hints are set; no-op otherwise.
        IReadOnlyList<string>? notableWorkHints = request.CompanionNameHints;
        if ((notableWorkHints is null || notableWorkHints.Count == 0) &&
            !string.IsNullOrWhiteSpace(request.TitleHint))
        {
            notableWorkHints = [request.TitleHint];
        }

        if (notableWorkHints is { Count: > 0 } hints && matches.Count > 1)
        {
            matches = await ReRankByCompanionHintsAsync(matches, hints, language, cancellationToken)
                .ConfigureAwait(false);
        }

        var best = matches[0];
        var normalizedScore = best.Score / 100.0;

        // A hit below the accept threshold is reported as not found, so nobody reads its occupations or
        // notable works. Skip the entity fetch that would only fill those in.
        if (normalizedScore < request.AcceptThreshold)
        {
            return new PersonSearchResult
            {
                Found = false,
                Qid = best.Id,
                CanonicalName = best.Name,
                Score = normalizedScore
            };
        }

        // Fetch the resolved entity's claims to populate occupations, notable works, and (optionally) group membership.
        var entities = await _ctx.EntityFetcher.FetchEntitiesAsync([best.Id], language, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<string> occupations = [];
        IReadOnlyList<string> notableWorks = [];
        var isGroup = false;
        IReadOnlyList<string>? groupMembers = null;

        if (entities.TryGetValue(best.Id, out var entity))
        {
            occupations = WikidataEntityFetcher.GetClaimValues(entity, "P106")
                .Select(dv => EntityMapper.MapDataValue(dv, "wikibase-item"))
                .Where(v => v.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(v.EntityId))
                .Select(v => v.EntityId!)
                .ToList();

            notableWorks = WikidataEntityFetcher.GetClaimValues(entity, "P800")
                .Select(dv => EntityMapper.MapDataValue(dv, "wikibase-item"))
                .Where(v => v.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(v.EntityId))
                .Select(v => v.EntityId!)
                .ToList();

            var entityTypes = WikidataEntityFetcher.GetTypeIds(entity, _ctx.Options.TypePropertyId);
            isGroup = entityTypes.Any(t =>
                string.Equals(t, MusicalGroupType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, MusicalEnsembleType, StringComparison.OrdinalIgnoreCase));

            if (isGroup && request.ExpandGroupMembers)
            {
                groupMembers = WikidataEntityFetcher.GetClaimValues(entity, "P527")
                    .Select(dv => EntityMapper.MapDataValue(dv, "wikibase-item"))
                    .Where(v => v.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(v.EntityId))
                    .Select(v => v.EntityId!)
                    .ToList();
            }
        }

        return new PersonSearchResult
        {
            Found = normalizedScore >= request.AcceptThreshold,
            Qid = best.Id,
            CanonicalName = best.Name,
            IsGroup = isGroup,
            Score = normalizedScore,
            Occupations = occupations,
            NotableWorks = notableWorks,
            GroupMembers = groupMembers
        };
    }

    /// <summary>
    /// Searches for multiple persons and returns one result per input request in
    /// the same order. Duplicate requests share one underlying lookup.
    /// </summary>
    public async Task<IReadOnlyList<PersonSearchResult>> SearchBatchAsync(
        IReadOnlyList<PersonSearchRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
            return [];

        var uniqueRequestsByKey = new Dictionary<string, PersonSearchRequest>(StringComparer.Ordinal);
        var requestKeys = new string[requests.Count];

        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

            var key = BuildBatchKey(request);
            requestKeys[i] = key;

            uniqueRequestsByKey.TryAdd(key, request);
        }

        var uniqueRequests = uniqueRequestsByKey.ToArray();
        var maxConcurrency = Math.Min(
            3,
            Math.Max(1, _ctx.Options.WikidataRateLimit.MaxConcurrentRequests));

        var lookups = await BoundedAsync.SelectAsync(uniqueRequests,
            Math.Min(maxConcurrency, Math.Max(1, _ctx.Options.MaxConcurrency)), async (pair, token) =>
        {
            var result = await SearchAsync(pair.Value, token).ConfigureAwait(false);
            return (pair.Key, Result: result);
        }, cancellationToken).ConfigureAwait(false);
        var resultsByKey = lookups.ToDictionary(
            item => item.Key,
            item => item.Result,
            StringComparer.Ordinal);

        return requestKeys.Select(key => resultsByKey[key]).ToList();
    }

    private static List<PropertyConstraint> BuildConstraints(PersonSearchRequest request, bool includeGroups)
    {
        var constraints = new List<PropertyConstraint>();

        // P106 (occupation) constraint based on role. Skipped when musical groups are included
        // because groups don't have P106 claims — the constraint would drag group candidates
        // below the accept threshold even when they are the correct answer.
        if (!includeGroups &&
            request.Role != PersonRole.Unknown &&
            RoleOccupations.TryGetValue(request.Role, out var occupations))
        {
            constraints.Add(new PropertyConstraint("P106", occupations));
        }

        // P800 (notable work) context via WorkQid.
        if (!string.IsNullOrEmpty(request.WorkQid))
        {
            constraints.Add(new PropertyConstraint("P800", request.WorkQid));
        }

        // P569 (date of birth) year hint.
        if (request.BirthYearHint.HasValue)
        {
            constraints.Add(new PropertyConstraint("P569", $"{request.BirthYearHint.Value:D4}-01-01"));
        }

        // P570 (date of death) year hint.
        if (request.DeathYearHint.HasValue)
        {
            constraints.Add(new PropertyConstraint("P570", $"{request.DeathYearHint.Value:D4}-01-01"));
        }

        return constraints;
    }

    private static string BuildBatchKey(PersonSearchRequest request)
    {
        var companions = request.CompanionNameHints is { Count: > 0 }
            ? string.Join("\u001f", request.CompanionNameHints.Select(NormalizeBatchKeyPart))
            : string.Empty;

        return string.Join("\u001e",
        [
            NormalizeBatchKeyPart(request.Name),
            request.Role.ToString(),
            NormalizeBatchKeyPart(request.TitleHint),
            NormalizeBatchKeyPart(request.WorkQid),
            request.IncludeMusicalGroups?.ToString() ?? "",
            request.BirthYearHint?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            request.DeathYearHint?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            request.ExpandGroupMembers.ToString(),
            NormalizeBatchKeyPart(request.Language),
            request.AcceptThreshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            companions
        ]);
    }

    private static string NormalizeBatchKeyPart(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    /// <summary>
    /// Re-ranks the top reconciliation candidates by how well their P800 (notable work) labels
    /// fuzzy-match the supplied companion name hints. One extra <c>wbgetentities</c> call fetches
    /// the candidates' P800 claims; a second batch call fetches labels for the referenced notable
    /// works; then each candidate's score is boosted by 10 points per hint that matches one of
    /// its notable works (token-sort ratio ≥ 75). Ties and non-matches fall back to the original
    /// ordering.
    /// </summary>
    private async Task<IReadOnlyList<ReconciliationResult>> ReRankByCompanionHintsAsync(
        IReadOnlyList<ReconciliationResult> matches,
        IReadOnlyList<string> hints,
        string language,
        CancellationToken cancellationToken)
    {
        // Fetch each candidate's entity data so we can read P800 (notable work) claims.
        var candidateQids = matches.Select(m => m.Id).ToList();
        var candidateEntities = await _ctx.EntityFetcher
            .FetchEntitiesAsync(candidateQids, language, cancellationToken)
            .ConfigureAwait(false);

        // Collect all distinct notable-work QIDs referenced across candidates.
        var candidateWorks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var allWorkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var qid in candidateQids)
        {
            if (!candidateEntities.TryGetValue(qid, out var entity))
            {
                candidateWorks[qid] = new List<string>();
                continue;
            }

            var works = WikidataEntityFetcher.GetClaimValues(entity, "P800")
                .Select(dv => EntityMapper.MapDataValue(dv, "wikibase-item"))
                .Where(v => v.Kind == WikidataValueKind.EntityId && !string.IsNullOrEmpty(v.EntityId))
                .Select(v => v.EntityId!)
                .ToList();

            candidateWorks[qid] = works;
            foreach (var w in works)
                allWorkIds.Add(w);
        }

        if (allWorkIds.Count == 0)
            return matches;

        // Batch-fetch labels for all referenced notable works in one call.
        var workLabels = await _labels.GetBatchAsync(allWorkIds.ToList(), language, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Rescore each candidate by companion hit count, then stable-sort.
        var scored = matches.Select(m =>
        {
            var works = candidateWorks[m.Id];
            var workLabelList = works
                .Select(w => workLabels.TryGetValue(w, out var l) ? l : null)
                .Where(l => !string.IsNullOrEmpty(l))
                .Select(l => l!)
                .ToList();

            var hitCount = 0;
            foreach (var hint in hints)
            {
                foreach (var label in workLabelList)
                {
                    if (FuzzyMatcher.TokenSortRatio(label, hint) >= 75)
                    {
                        hitCount++;
                        break;
                    }
                }
            }

            return (Match: m, BoostedScore: m.Score + hitCount * 10.0);
        }).ToList();

        scored.Sort((a, b) => b.BoostedScore.CompareTo(a.BoostedScore));
        return scored.Select(s => s.Match).ToList();
    }
}
