using Tuvima.Wikidata.Internal;
using Tuvima.Wikidata.Internal.Json;

namespace Tuvima.Wikidata.Services;

/// <summary>
/// Wikipedia article URL resolution, summaries, and section content extraction.
/// Obtained via <see cref="WikidataReconciler.Wikipedia"/>.
/// </summary>
public sealed class WikipediaService
{
    private readonly ReconcilerContext _ctx;

    internal WikipediaService(ReconcilerContext ctx) => _ctx = ctx;

    /// <summary>
    /// Resolves Wikipedia article URLs for the given QIDs.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetWikipediaUrlsAsync(
        IReadOnlyList<string> qids, string language = "en", CancellationToken cancellationToken = default)
    {
        var entities = await _ctx.EntityFetcher.FetchEntitiesWithSitelinksAsync(qids, language, cancellationToken)
            .ConfigureAwait(false);

        var siteKey = $"{language}wiki";
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, entity) in entities)
        {
            if (entity.Sitelinks?.TryGetValue(siteKey, out var sitelink) == true &&
                !string.IsNullOrEmpty(sitelink.Title))
            {
                result[id] = $"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(sitelink.Title)}";
            }
            else
            {
                _ctx.Diagnostics.RecordFailure(
                    WikidataFailureKind.NoSitelink,
                    "wikipedia.sitelink",
                    $"No {language} Wikipedia sitelink exists for {id}.",
                    id);
            }
        }

        return result;
    }

    /// <summary>
    /// Fetches Wikipedia article summaries for the given QIDs.
    /// </summary>
    public async Task<IReadOnlyList<WikipediaSummary>> GetWikipediaSummariesAsync(
        IReadOnlyList<string> qids, string language = "en", CancellationToken cancellationToken = default)
    {
        var entities = await _ctx.EntityFetcher.FetchEntitiesWithSitelinksAsync(qids, language, cancellationToken)
            .ConfigureAwait(false);

        var siteKey = $"{language}wiki";
        var titleToQid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, entity) in entities)
        {
            if (entity.Sitelinks?.TryGetValue(siteKey, out var sitelink) == true &&
                !string.IsNullOrEmpty(sitelink.Title))
            {
                titleToQid[sitelink.Title] = id;
            }
            else
            {
                _ctx.Diagnostics.RecordFailure(
                    WikidataFailureKind.NoSitelink,
                    "wikipedia.summary",
                    $"No {language} Wikipedia sitelink exists for {id}.",
                    id);
            }
        }

        if (titleToQid.Count == 0)
            return [];

        return await FetchSummaryBatchesAsync(titleToQid, language, includeLanguage: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches Wikipedia summaries with language fallback.
    /// </summary>
    public async Task<IReadOnlyList<WikipediaSummary>> GetWikipediaSummariesAsync(
        IReadOnlyList<string> qids, string language,
        IReadOnlyList<string>? fallbackLanguages, CancellationToken cancellationToken = default)
    {
        var langChain = fallbackLanguages is { Count: > 0 }
            ? new List<string> { language }.Concat(fallbackLanguages.Where(l => !string.Equals(l, language, StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : LanguageFallback.GetFallbackChain(language);

        var entities = await _ctx.EntityFetcher.FetchEntitiesWithSitelinksAsync(qids, language, cancellationToken)
            .ConfigureAwait(false);

        var qidToLangTitle = new Dictionary<string, (string Language, string Title)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, entity) in entities)
        {
            if (entity.Sitelinks is not null)
            {
                foreach (var lang in langChain)
                {
                    var siteKey = $"{lang}wiki";
                    if (entity.Sitelinks.TryGetValue(siteKey, out var sitelink) && !string.IsNullOrEmpty(sitelink.Title))
                    {
                        qidToLangTitle[id] = (lang, sitelink.Title);
                        break;
                    }
                }
            }

            if (!qidToLangTitle.ContainsKey(id))
            {
                _ctx.Diagnostics.RecordFailure(
                    WikidataFailureKind.NoSitelink,
                    "wikipedia.summary",
                    $"No Wikipedia sitelink exists for {id} in the configured fallback chain.",
                    id);
            }
        }

        if (qidToLangTitle.Count == 0)
            return [];

        var grouped = qidToLangTitle
            .GroupBy(kvp => kvp.Value.Language, StringComparer.OrdinalIgnoreCase).ToArray();
        var fetched = await BoundedAsync.SelectAsync(grouped, _ctx.Options.MaxConcurrency,
            (group, token) =>
            {
                var titleToQid = group.ToDictionary(
                    kvp => kvp.Value.Title,
                    kvp => kvp.Key,
                    StringComparer.OrdinalIgnoreCase);
                return FetchSummaryBatchesAsync(titleToQid, group.Key, includeLanguage: true, token);
            }, cancellationToken).ConfigureAwait(false);
        return fetched.SelectMany(s => s).ToList();
    }

    /// <summary>
    /// Fetches one QID-based Wikipedia summary with language fallback and a typed not-found result.
    /// </summary>
    public async Task<WikipediaSummaryResult> GetWikipediaSummaryAsync(
        string qid,
        string language = "en",
        IReadOnlyList<string>? fallbackLanguages = null,
        CancellationToken cancellationToken = default)
    {
        var batch = await GetWikipediaSummaryResultsAsync([qid], language, fallbackLanguages, cancellationToken)
            .ConfigureAwait(false);
        return batch.TryGetValue(qid, out var result)
            ? result
            : new WikipediaSummaryResult
            {
                EntityId = qid,
                Found = false,
                Language = language,
                FailureKind = WikidataFailureKind.NotFound,
                FailureMessage = "No Wikipedia summary result was produced."
            };
    }

    /// <summary>
    /// Fetches QID-based Wikipedia summaries with preferred language fallback and one result per input QID.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, WikipediaSummaryResult>> GetWikipediaSummaryResultsAsync(
        IReadOnlyList<string> qids,
        string language = "en",
        IReadOnlyList<string>? fallbackLanguages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(qids);
        var distinct = qids
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summaries = fallbackLanguages is null
            ? await GetWikipediaSummariesAsync(distinct, language, cancellationToken).ConfigureAwait(false)
            : await GetWikipediaSummariesAsync(distinct, language, fallbackLanguages, cancellationToken).ConfigureAwait(false);

        var byQid = summaries.ToDictionary(s => s.EntityId, StringComparer.OrdinalIgnoreCase);
        var results = new Dictionary<string, WikipediaSummaryResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var qid in distinct)
        {
            if (byQid.TryGetValue(qid, out var summary))
            {
                results[qid] = new WikipediaSummaryResult
                {
                    EntityId = qid,
                    Found = true,
                    Summary = summary.Extract,
                    PageTitle = summary.Title,
                    PageUrl = summary.ArticleUrl,
                    Language = summary.Language ?? language,
                    SourceProvider = "wikipedia"
                };
            }
            else
            {
                results[qid] = new WikipediaSummaryResult
                {
                    EntityId = qid,
                    Found = false,
                    Language = language,
                    FailureKind = WikidataFailureKind.NoSitelink,
                    FailureMessage = "No Wikipedia page or summary exists for the requested QID in the configured language fallback chain."
                };
            }
        }

        return results;
    }

    /// <summary>
    /// Fetches structured Wikidata/Wikipedia descriptions without blending them with app-side retail data.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, WikidataDescriptionResult>> GetDescriptionsAsync(
        IReadOnlyList<string> qids,
        string language = "en",
        IReadOnlyList<string>? fallbackLanguages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(qids);
        var distinct = qids
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var entitiesTask = _ctx.EntityFetcher.FetchEntitiesAsync(distinct, language, cancellationToken);
        var summariesTask = GetWikipediaSummaryResultsAsync(distinct, language, fallbackLanguages, cancellationToken);

        await Task.WhenAll(entitiesTask, summariesTask).ConfigureAwait(false);

        var entities = await entitiesTask.ConfigureAwait(false);
        var summaries = await summariesTask.ConfigureAwait(false);
        var results = new Dictionary<string, WikidataDescriptionResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var qid in distinct)
        {
            entities.TryGetValue(qid, out var entity);
            summaries.TryGetValue(qid, out var summary);

            WikidataEntityInfo? mapped = entity is null ? null : EntityMapper.MapEntity(entity, language);
            results[qid] = new WikidataDescriptionResult
            {
                EntityId = qid,
                Found = mapped is not null || summary?.Found == true,
                WikipediaDescription = summary?.Summary,
                WikidataDescription = mapped?.Description,
                ShortDescription = summary?.Found == true ? summary.PageTitle : null,
                Aliases = mapped?.Aliases ?? [],
                Language = summary?.Language ?? language,
                FailureKind = mapped is null && summary?.Found != true ? WikidataFailureKind.NotFound : null,
                FailureMessage = mapped is null && summary?.Found != true
                    ? "No Wikidata entity or Wikipedia description was found for the requested QID."
                    : null
            };
        }

        return results;
    }

    private async Task<IReadOnlyList<WikipediaSummary>> FetchSummaryBatchesAsync(
        IReadOnlyDictionary<string, string> titleToQid,
        string language,
        bool includeLanguage,
        CancellationToken cancellationToken)
    {
        var results = new List<WikipediaSummary>();
        var batchSize = Math.Clamp(_ctx.Options.WikipediaRateLimit.MaxBatchSize, 1, 50);
        var titles = titleToQid.Keys
            .OrderBy(title => title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < titles.Count; i += batchSize)
        {
            var batch = titles.Skip(i).Take(batchSize).ToList();
            _ctx.Diagnostics.RecordBatch("wikipedia.summary", batch.Count);

            var titlesParam = string.Join('|', batch);
            var url = $"https://{language}.wikipedia.org/w/api.php?action=query" +
                      "&prop=extracts|pageimages|info|description" +
                      "&exintro=1&explaintext=1&pithumbsize=320&piprop=thumbnail&inprop=url" +
                      $"&redirects=1&format=json&formatversion=2&titles={Uri.EscapeDataString(titlesParam)}";

            var json = await _ctx.ResilientClient.GetStringAsync(url, cancellationToken)
                .ConfigureAwait(false);
            var response = ProviderJson.Deserialize(
                json,
                WikidataJsonContext.Default.WikipediaSummaryBatchResponse,
                "wikipedia.summary");

            if (response?.Query?.Pages is not { Count: > 0 } pages)
                continue;

            var titleLookup = BuildTitleLookup(titleToQid, response.Query);

            foreach (var page in pages)
            {
                if (page.Missing || string.IsNullOrWhiteSpace(page.Extract))
                {
                    if (TryResolveQidForTitle(page.Title, titleLookup, out var missingQid))
                    {
                        _ctx.Diagnostics.RecordFailure(
                            WikidataFailureKind.NotFound,
                            "wikipedia.summary",
                            $"Wikipedia summary was not found for {page.Title}.",
                            missingQid);
                    }
                    continue;
                }

                if (!TryResolveQidForTitle(page.Title, titleLookup, out var qid))
                    continue;

                results.Add(new WikipediaSummary
                {
                    EntityId = qid,
                    Title = page.Title,
                    Extract = page.Extract,
                    Description = page.Description,
                    ThumbnailUrl = page.Thumbnail?.Source,
                    ArticleUrl = page.FullUrl
                        ?? $"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(page.Title)}",
                    Language = includeLanguage ? language : null
                });
            }
        }

        return results;
    }

    private static Dictionary<string, string> BuildTitleLookup(
        IReadOnlyDictionary<string, string> titleToQid,
        WikipediaSummaryBatchQuery query)
    {
        var lookup = new Dictionary<string, string>(titleToQid, StringComparer.OrdinalIgnoreCase);
        AddTitleMappings(lookup, query.Normalized);
        AddTitleMappings(lookup, query.Redirects);
        return lookup;
    }

    private static void AddTitleMappings(Dictionary<string, string> lookup, List<WikipediaTitleMap>? mappings)
    {
        if (mappings is null)
            return;

        foreach (var mapping in mappings)
        {
            if (lookup.TryGetValue(mapping.From, out var qid))
                lookup[mapping.To] = qid;
        }
    }

    private static bool TryResolveQidForTitle(
        string title,
        IReadOnlyDictionary<string, string> titleLookup,
        out string qid)
    {
        if (titleLookup.TryGetValue(title, out qid!))
            return true;

        qid = "";
        return false;
    }

    /// <summary>
    /// Gets the table of contents for Wikipedia articles of the specified entities.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<WikipediaSection>>> GetWikipediaSectionsAsync(
        IReadOnlyList<string> qids, string language = "en",
        CancellationToken cancellationToken = default)
    {
        var entities = await _ctx.EntityFetcher.FetchEntitiesWithSitelinksAsync(qids, language, cancellationToken)
            .ConfigureAwait(false);

        var siteKey = $"{language}wiki";
        var titleToQid = new Dictionary<string, string>();

        foreach (var (id, entity) in entities)
        {
            if (entity.Sitelinks?.TryGetValue(siteKey, out var sitelink) == true &&
                !string.IsNullOrEmpty(sitelink.Title))
            {
                titleToQid[sitelink.Title] = id;
            }
            else
            {
                _ctx.Diagnostics.RecordFailure(
                    WikidataFailureKind.NoSitelink,
                    "wikipedia.sections",
                    $"No {language} Wikipedia sitelink exists for {id}.",
                    id);
            }
        }

        if (titleToQid.Count == 0)
            return new Dictionary<string, IReadOnlyList<WikipediaSection>>();

        var result = new Dictionary<string, IReadOnlyList<WikipediaSection>>(StringComparer.OrdinalIgnoreCase);

        var fetched = await BoundedAsync.SelectAsync(titleToQid.ToArray(), _ctx.Options.MaxConcurrency, async (kvp, token) =>
        {
            try
            {
                var url = $"https://{language}.wikipedia.org/w/api.php?action=parse" +
                          $"&page={Uri.EscapeDataString(kvp.Key)}&prop=tocdata&format=json";
                var json = await _ctx.ResilientClient.GetStringAsync(url, token)
                    .ConfigureAwait(false);
                var response = ProviderJson.Deserialize(
                    json,
                    WikidataJsonContext.Default.ParseResponse,
                    "parse.toc");

                if (response?.Parse?.TocData?.Sections is { Count: > 0 } sections)
                {
                    return (Qid: kvp.Value, Sections: (IReadOnlyList<WikipediaSection>)sections
                        .Select(s => new WikipediaSection
                        {
                            Title = HtmlTextExtractor.StripInlineHtml(s.Line),
                            Index = int.TryParse(s.Index, out var idx) ? idx : 0,
                            Level = s.HLevel,
                            Number = s.Number,
                            Anchor = s.Anchor
                        })
                        .ToList());
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WikidataProviderException)
            {
                throw;
            }
            catch
            {
                // Skip on failure
            }
            return (Qid: kvp.Value, Sections: (IReadOnlyList<WikipediaSection>?)null);
        }, cancellationToken).ConfigureAwait(false);
        foreach (var item in fetched)
        {
            if (item.Sections is not null)
                result[item.Qid] = item.Sections;
        }

        return result;
    }

    /// <summary>
    /// Fetches the content of a specific Wikipedia article section as plain text.
    /// </summary>
    public async Task<string?> GetWikipediaSectionContentAsync(
        string qid, int sectionIndex, string language = "en",
        CancellationToken cancellationToken = default)
    {
        var pageTitle = await ResolveWikipediaTitle(qid, language, cancellationToken).ConfigureAwait(false);
        if (pageTitle is null)
            return null;

        var text = await FetchSectionText(pageTitle, sectionIndex, language, cancellationToken)
            .ConfigureAwait(false);
        if (text is null)
            return null;

        text = HtmlTextExtractor.StripLeadingHeading(text);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>
    /// Fetches a Wikipedia section and all its subsections as a structured list.
    /// </summary>
    public async Task<IReadOnlyList<SectionContent>?> GetWikipediaSectionWithSubsectionsAsync(
        string qid, int sectionIndex, string language = "en",
        CancellationToken cancellationToken = default)
    {
        var toc = await GetWikipediaSectionsAsync([qid], language, cancellationToken).ConfigureAwait(false);
        if (!toc.TryGetValue(qid, out var sections))
            return null;

        var targetSection = sections.FirstOrDefault(s => s.Index == sectionIndex);
        if (targetSection is null)
            return null;

        var sectionIndices = new List<(int Index, string Title)> { (targetSection.Index, targetSection.Title) };

        var foundTarget = false;
        foreach (var s in sections)
        {
            if (s.Index == sectionIndex)
            {
                foundTarget = true;
                continue;
            }
            if (!foundTarget)
                continue;
            if (s.Level <= targetSection.Level)
                break;
            sectionIndices.Add((s.Index, s.Title));
        }

        var pageTitle = await ResolveWikipediaTitle(qid, language, cancellationToken).ConfigureAwait(false);
        if (pageTitle is null)
            return null;

        var results = await BoundedAsync.SelectAsync(sectionIndices, _ctx.Options.MaxConcurrency, async (si, token) =>
        {
            var text = await FetchSectionText(pageTitle, si.Index, language, token)
                .ConfigureAwait(false);
            if (text is not null)
                text = HtmlTextExtractor.StripLeadingHeading(text);
            return (si.Title, Content: string.IsNullOrWhiteSpace(text) ? null : text!.Trim());
        }, cancellationToken).ConfigureAwait(false);

        var content = results
            .Where(r => r.Content is not null)
            .Select(r => new SectionContent { Title = r.Title, Content = r.Content! })
            .ToList();

        return content.Count > 0 ? content : null;
    }

    private async Task<string?> ResolveWikipediaTitle(string qid, string language, CancellationToken cancellationToken)
    {
        var entities = await _ctx.EntityFetcher.FetchEntitiesWithSitelinksAsync([qid], language, cancellationToken)
            .ConfigureAwait(false);

        var siteKey = $"{language}wiki";
        if (!entities.TryGetValue(qid, out var entity) ||
            entity.Sitelinks?.TryGetValue(siteKey, out var sitelink) != true ||
            string.IsNullOrEmpty(sitelink!.Title))
        {
            _ctx.Diagnostics.RecordFailure(
                WikidataFailureKind.NoSitelink,
                "wikipedia.sitelink",
                $"No {language} Wikipedia sitelink exists for {qid}.",
                qid);
            return null;
        }

        return sitelink.Title;
    }

    private async Task<string?> FetchSectionText(string pageTitle, int sectionIndex, string language, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://{language}.wikipedia.org/w/api.php?action=parse" +
                      $"&page={Uri.EscapeDataString(pageTitle)}&section={sectionIndex}" +
                      "&prop=text&format=json";
            var json = await _ctx.ResilientClient.GetStringAsync(url, cancellationToken)
                .ConfigureAwait(false);
            var response = ProviderJson.Deserialize(
                json,
                WikidataJsonContext.Default.ParseResponse,
                "parse.section");

            if (response?.Error is not null || response?.Parse?.Text?.Html is null)
                return null;

            var text = HtmlTextExtractor.ExtractText(response.Parse.Text.Html);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WikidataProviderException ex) when (ex.Kind == WikidataFailureKind.NotFound)
        {
            // "nosuchsection" / "missingtitle": the documented contract is a null result.
            return null;
        }
        catch (WikidataProviderException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
