using static Tuvima.Wikidata.Internal.BridgeEntityFacts;

namespace Tuvima.Wikidata.Internal;

/// <summary>Scores verified bridge and text candidates without provider I/O.</summary>
internal class BridgeCandidateScorer
{
    private static readonly IReadOnlyDictionary<BridgeMediaKind, IReadOnlyList<string>> MediaTypeHints =
        new Dictionary<BridgeMediaKind, IReadOnlyList<string>>
        {
            [BridgeMediaKind.Book] = ["Q571", "Q7725634", "Q47461344", "Q3331189", "Q8261", "Q149537", "Q49084"],
            [BridgeMediaKind.Audiobook] = ["Q742421", "Q3331189", "Q571"],
            [BridgeMediaKind.ComicSeries] = ["Q14406742", "Q3297186", "Q21198342", "Q838795", "Q1004"],
            [BridgeMediaKind.ComicIssue] = ["Q1114461", "Q14406742", "Q3297186", "Q21198342", "Q838795", "Q1004"],
            [BridgeMediaKind.Movie] = ["Q11424"],
            [BridgeMediaKind.TvSeries] = ["Q5398426"],
            [BridgeMediaKind.TvSeason] = ["Q3464665"],
            [BridgeMediaKind.TvEpisode] = ["Q21191270"],
            [BridgeMediaKind.MusicAlbum] = ["Q482994"],
            [BridgeMediaKind.MusicRelease] = ["Q2031291", "Q482994"],
            [BridgeMediaKind.MusicRecording] = ["Q2188189"],
            [BridgeMediaKind.MusicWork] = ["Q2188189", "Q7366"],
            [BridgeMediaKind.MusicTrack] = ["Q7302866", "Q2188189", "Q7366"],
            [BridgeMediaKind.Game] = ["Q7889"],
            [BridgeMediaKind.App] = ["Q7397"]
        };

    private readonly string _typePropertyId;

    public BridgeCandidateScorer(string typePropertyId) => _typePropertyId = typePropertyId;

    public List<BridgeCandidate> BuildBridgeCandidates(
        BridgeResolutionRequest request,
        IReadOnlyList<ResolvedBridgeIdentifier> identifiers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> lookupResults,
        IReadOnlyDictionary<string, WikidataEntityInfo> entities,
        BridgeDiagnosticsBuilder diagnostics,
        IReadOnlyDictionary<string, string?> hintLabels)
    {
        var qidToMatches = new Dictionary<string, List<ResolvedBridgeIdentifier>>(StringComparer.OrdinalIgnoreCase);

        foreach (var identifier in identifiers)
        {
            if (!lookupResults.TryGetValue(identifier.LookupKey, out var qids) || qids.Count == 0)
                continue;

            diagnostics.MatchedProperties.Add(identifier.PropertyId);

            foreach (var qid in qids)
            {
                if (!qidToMatches.TryGetValue(qid, out var matches))
                {
                    matches = [];
                    qidToMatches[qid] = matches;
                }

                matches.Add(identifier);
            }
        }

        var candidates = new List<BridgeCandidate>();
        foreach (var (qid, matches) in qidToMatches)
        {
            if (!entities.TryGetValue(qid, out var entity))
            {
                diagnostics.RejectedCandidates.Add($"{qid}:entity-not-returned");
                continue;
            }

            var verifiedMatches = matches
                .Where(m => ClaimHasValue(entity, m.PropertyId, m.NormalizedValue))
                .ToList();

            if (verifiedMatches.Count == 0)
            {
                diagnostics.RejectedCandidates.Add($"{qid}:bridge-claim-not-verified");
                continue;
            }

            candidates.Add(BuildCandidate(request, entity, verifiedMatches, diagnostics, hintLabels));
        }

        return SortCandidates(candidates);
    }

    private BridgeCandidate BuildCandidate(
        BridgeResolutionRequest request,
        WikidataEntityInfo entity,
        IReadOnlyList<ResolvedBridgeIdentifier> matches,
        BridgeDiagnosticsBuilder diagnostics,
        IReadOnlyDictionary<string, string?> hintLabels)
    {
        var reasonCodes = new List<string> { "bridge.exact" };
        var warnings = new List<string>();
        var entityTypes = GetEntityIds(entity, _typePropertyId);
        var typeScore = ScoreMediaType(request.MediaKind, entityTypes, reasonCodes, warnings);
        var titleScore = ScoreTitle(request.Title, entity, reasonCodes, warnings);
        var creatorScore = ScoreLinkedEntityHint(
            request.Creator,
            entity,
            hintLabels,
            CreatorPropertyIds,
            "creator",
            reasonCodes,
            warnings,
            strongScore: 0.05,
            partialScore: 0.03);
        var seriesScore = ScoreLinkedEntityHint(
            request.SeriesTitle,
            entity,
            hintLabels,
            SeriesPropertyIds,
            "series",
            reasonCodes,
            warnings,
            strongScore: 0.04,
            partialScore: 0.02);
        var yearScore = ScoreYear(request.Year, entity, reasonCodes);
        var ordinalScore = ScoreOrdinalHints(request, entity, reasonCodes, warnings);
        var bridgeScore = Math.Min(0.74 + (matches.Count - 1) * 0.03, 0.82);
        var confidence = Math.Clamp(bridgeScore + typeScore + titleScore + creatorScore + seriesScore + yearScore + ordinalScore, 0, 1);
        var firstMatch = matches[0];
        var collected = CollectKnownBridgeIds(entity, request, matches);

        foreach (var warning in warnings)
            diagnostics.Warnings.Add($"{entity.Id}:{warning}");

        return new BridgeCandidate
        {
            Qid = entity.Id,
            Label = entity.Label,
            Description = entity.Description,
            EntityTypes = entityTypes,
            MatchedBridgeIdType = firstMatch.RawKey,
            MatchedPropertyId = firstMatch.PropertyId,
            MatchedBridgeValue = firstMatch.NormalizedValue,
            Confidence = Math.Round(confidence, 4),
            ReasonCodes = reasonCodes,
            Warnings = warnings,
            CollectedBridgeIds = collected
        };
    }

    public static List<BridgeCandidate> SortCandidates(IEnumerable<BridgeCandidate> candidates)
    {
        return candidates
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => QidNumber(c.Qid))
            .ToList();
    }

    private static double ScoreMediaType(
        BridgeMediaKind mediaKind,
        IReadOnlyList<string> entityTypes,
        List<string> reasonCodes,
        List<string> warnings)
    {
        var expected = GetMediaTypeHints(mediaKind);
        if (expected.Count == 0)
        {
            reasonCodes.Add("type.unchecked");
            return 0;
        }

        if (entityTypes.Any(t => expected.Contains(t, StringComparer.OrdinalIgnoreCase)))
        {
            reasonCodes.Add("type.match");
            return 0.12;
        }

        if (entityTypes.Count == 0)
        {
            warnings.Add("type.missing");
            return 0;
        }

        warnings.Add("type.mismatch");
        return -0.10;
    }

    private static double ScoreTitle(
        string? title,
        WikidataEntityInfo entity,
        List<string> reasonCodes,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(title))
            return 0;

        var labels = new List<string>();
        if (!string.IsNullOrWhiteSpace(entity.Label))
            labels.Add(entity.Label);
        labels.AddRange(entity.Aliases);

        var best = labels.Count == 0
            ? 0
            : labels.Max(label => FuzzyMatcher.TokenSortRatio(title, label));

        if (best >= 95)
        {
            reasonCodes.Add("title.exact");
            return 0.09;
        }

        if (best >= 85)
        {
            reasonCodes.Add("title.strong");
            return 0.06;
        }

        if (best >= 70)
        {
            reasonCodes.Add("title.partial");
            return 0.03;
        }

        warnings.Add("title.weak");
        return 0;
    }

    private static double ScoreLinkedEntityHint(
        string? hint,
        WikidataEntityInfo entity,
        IReadOnlyDictionary<string, string?> labels,
        IReadOnlyList<string> propertyIds,
        string reasonPrefix,
        List<string> reasonCodes,
        List<string> warnings,
        double strongScore,
        double partialScore)
    {
        if (string.IsNullOrWhiteSpace(hint))
            return 0;

        var candidateLabels = propertyIds
            .SelectMany(propertyId => GetEntityIds(entity, propertyId))
            .Select(qid => labels.TryGetValue(qid, out var label) ? label : null)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label!)
            .ToList();

        if (candidateLabels.Count == 0)
            return 0;

        var best = candidateLabels.Max(label => FuzzyMatcher.TokenSortRatio(hint, label));
        if (best >= 90)
        {
            reasonCodes.Add($"{reasonPrefix}.strong");
            return strongScore;
        }

        if (best >= 75)
        {
            reasonCodes.Add($"{reasonPrefix}.partial");
            return partialScore;
        }

        warnings.Add($"{reasonPrefix}.weak");
        return 0;
    }

    private static double ScoreYear(int? year, WikidataEntityInfo entity, List<string> reasonCodes)
    {
        if (year is null)
            return 0;

        foreach (var propertyId in new[] { "P577", "P571", "P580" })
        {
            if (!entity.Claims.TryGetValue(propertyId, out var claims))
                continue;

            foreach (var claim in claims)
            {
                if (TryParseWikidataYear(claim.Value?.RawValue, out var candidateYear) &&
                    candidateYear == year.Value)
                {
                    reasonCodes.Add("year.match");
                    return 0.04;
                }
            }
        }

        return 0;
    }

    private static double ScoreOrdinalHints(
        BridgeResolutionRequest request,
        WikidataEntityInfo entity,
        List<string> reasonCodes,
        List<string> warnings)
    {
        return request.MediaKind switch
        {
            BridgeMediaKind.TvSeason => ScoreOrdinalHint(
                request.SeasonNumber,
                entity,
                "season",
                directProperties: ["P4908", "P1545"],
                qualifierProperties: ["P1545", "P4908"],
                reasonCodes,
                warnings,
                matchScore: 0.06,
                mismatchScore: -0.04),

            BridgeMediaKind.TvEpisode => ScoreOrdinalHint(
                    request.EpisodeNumber,
                    entity,
                    "episode",
                    directProperties: ["P1545"],
                    qualifierProperties: ["P1545"],
                    reasonCodes,
                    warnings,
                    matchScore: 0.06,
                    mismatchScore: -0.04)
                + ScoreOrdinalHint(
                    request.SeasonNumber,
                    entity,
                    "season",
                    directProperties: ["P4908"],
                    qualifierProperties: ["P4908"],
                    reasonCodes,
                    warnings,
                    matchScore: 0.03,
                    mismatchScore: -0.02),

            BridgeMediaKind.ComicIssue => ScoreOrdinalHint(
                request.IssueNumber,
                entity,
                "issue",
                directProperties: ["P433", "P1545"],
                qualifierProperties: ["P1545"],
                reasonCodes,
                warnings,
                matchScore: 0.06,
                mismatchScore: -0.04),

            _ => 0
        };
    }

    private static double ScoreOrdinalHint(
        object? hint,
        WikidataEntityInfo entity,
        string reasonPrefix,
        IReadOnlyList<string> directProperties,
        IReadOnlyList<string> qualifierProperties,
        List<string> reasonCodes,
        List<string> warnings,
        double matchScore,
        double mismatchScore)
    {
        var normalizedHint = NormalizeOrdinal(hint);
        if (string.IsNullOrWhiteSpace(normalizedHint))
            return 0;

        var ordinals = CollectOrdinalValues(entity, directProperties, qualifierProperties)
            .Select(NormalizeOrdinal)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ordinals.Count == 0)
        {
            warnings.Add($"{reasonPrefix}.ordinal.missing");
            return 0;
        }

        if (ordinals.Any(value => OrdinalEquals(value, normalizedHint)))
        {
            reasonCodes.Add($"{reasonPrefix}.ordinal.match");
            return matchScore;
        }

        warnings.Add($"{reasonPrefix}.ordinal.mismatch");
        return mismatchScore;
    }

    private static List<string> CollectOrdinalValues(
        WikidataEntityInfo entity,
        IReadOnlyList<string> directProperties,
        IReadOnlyList<string> qualifierProperties)
    {
        var values = new List<string>();

        foreach (var propertyId in directProperties)
        {
            if (!entity.Claims.TryGetValue(propertyId, out var claims))
                continue;

            foreach (var claim in claims)
            {
                if (!string.IsNullOrWhiteSpace(claim.Value?.RawValue))
                    values.Add(claim.Value.RawValue);
            }
        }

        foreach (var claims in entity.Claims.Values)
        {
            foreach (var claim in claims)
            {
                foreach (var propertyId in qualifierProperties)
                {
                    if (!claim.Qualifiers.TryGetValue(propertyId, out var qualifierValues))
                        continue;

                    foreach (var value in qualifierValues)
                    {
                        if (!string.IsNullOrWhiteSpace(value.RawValue))
                            values.Add(value.RawValue);
                    }
                }
            }
        }

        return values;
    }

    private static string? NormalizeOrdinal(object? value)
    {
        if (value is null)
            return null;

        var text = value switch
        {
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            short s => s.ToString(System.Globalization.CultureInfo.InvariantCulture),
            byte b => b.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString()
        };

        if (string.IsNullOrWhiteSpace(text))
            return null;

        var compact = new string(text
            .Trim()
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(compact))
            return null;

        return compact.All(char.IsDigit)
            && long.TryParse(compact, out var numeric)
                ? numeric.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : compact;
    }

    private static bool OrdinalEquals(string candidate, string hint)
    {
        if (string.Equals(candidate, hint, StringComparison.OrdinalIgnoreCase))
            return true;

        return long.TryParse(candidate, out var candidateNumber)
            && long.TryParse(hint, out var hintNumber)
            && candidateNumber == hintNumber;
    }

    private static bool ClaimHasValue(WikidataEntityInfo entity, string propertyId, string normalizedValue)
    {
        if (!entity.Claims.TryGetValue(propertyId, out var claims))
            return false;

        return claims.Any(claim =>
            claim.Value is not null &&
            string.Equals(
                NormalizeClaimValue(propertyId, claim.Value.RawValue),
                normalizedValue,
                StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> CollectKnownBridgeIds(
        WikidataEntityInfo entity,
        BridgeResolutionRequest request,
        IReadOnlyList<ResolvedBridgeIdentifier> matches)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in matches)
        {
            var value = GetFirstRawValue(entity, match.PropertyId);
            if (!string.IsNullOrWhiteSpace(value))
                result[match.RawKey] = value;
        }

        var customProperties = request.CustomWikidataProperties?.Values ?? [];
        var propertyIds = BridgeIdCatalog.GetKnownPropertyIds(request.MediaKind)
            .Concat(customProperties)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var propertyId in propertyIds)
        {
            if (!entity.Claims.TryGetValue(propertyId, out var claims))
                continue;

            var value = claims.FirstOrDefault(c => c.Value is not null)?.Value?.RawValue;
            if (!string.IsNullOrWhiteSpace(value))
                result.TryAdd(propertyId, value);
        }

        return result;
    }

    private static string NormalizeClaimValue(string propertyId, string value)
    {
        var trimmed = value.Trim();
        return propertyId switch
        {
            "P212" or "P957" => new string(trimmed.Where(c => char.IsDigit(c) || c is 'X' or 'x').ToArray()).ToUpperInvariant(),
            "P345" => trimmed.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? trimmed.ToLowerInvariant() : $"tt{trimmed.PadLeft(7, '0')}",
            "P4947" or "P4983" or "P4835" or "P7043" or "P6395" or "P2281" or "P2850" or "P10110" or "P9586" or "P9751" or "P9750" or "P6381" or "P6398" or "P3861" => new string(trimmed.Where(char.IsDigit).ToArray()),
            "P435" or "P436" or "P5813" or "P4404" => trimmed.ToLowerInvariant(),
            "P648" => trimmed.ToUpperInvariant(),
            _ => trimmed
        };
    }

    private static bool TryParseWikidataYear(string? raw, out int year)
    {
        year = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.TrimStart('+');
        return trimmed.Length >= 4 && int.TryParse(trimmed[..4], out year);
    }

    public static IReadOnlyList<string> GetMediaTypeHints(BridgeMediaKind mediaKind)
    {
        return MediaTypeHints.TryGetValue(mediaKind, out var types) ? types : [];
    }

    public BridgeCandidate BuildTextCandidate(BridgeResolutionRequest request, ReconciliationResult match,
        WikidataEntityInfo entity, IReadOnlyDictionary<string, string?> hintLabels)
    {
        var reasonCodes = new List<string> { "text.fallback" };
        var warnings = new List<string>();
        var entityTypes = GetEntityIds(entity, _typePropertyId);
        var score = Math.Clamp(match.Score / 100.0, 0, 1);
        score += ScoreMediaType(request.MediaKind, entityTypes, reasonCodes, warnings);
        score += ScoreLinkedEntityHint(
            request.Creator,
            entity,
            hintLabels,
            CreatorPropertyIds,
            "creator",
            reasonCodes,
            warnings,
            strongScore: 0.05,
            partialScore: 0.03);
        score += ScoreLinkedEntityHint(
            request.SeriesTitle,
            entity,
            hintLabels,
            SeriesPropertyIds,
            "series",
            reasonCodes,
            warnings,
            strongScore: 0.04,
            partialScore: 0.02);
        score += ScoreYear(request.Year, entity, reasonCodes);
        score += ScoreOrdinalHints(request, entity, reasonCodes, warnings);

        return new BridgeCandidate
        {
            Qid = entity.Id,
            Label = entity.Label ?? match.Name,
            Description = entity.Description ?? match.Description,
            EntityTypes = entityTypes,
            Confidence = Math.Round(Math.Clamp(score, 0, 1), 4),
            ReasonCodes = reasonCodes,
            Warnings = warnings,
            CollectedBridgeIds = CollectKnownBridgeIds(entity, request, [])
        };
    }
}
