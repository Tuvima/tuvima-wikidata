using System.Text.Json;
using Tuvima.Wikidata.Internal.Json;

namespace Tuvima.Wikidata.Internal;

/// <summary>
/// Fetches entity data (labels, descriptions, claims) from the Wikidata API using wbgetentities.
/// </summary>
internal sealed class WikidataEntityFetcher
{
    private const int ProviderMaxIdsPerRequest = 50;

    private readonly ResilientHttpClient _httpClient;
    private readonly WikidataReconcilerOptions _options;
    private readonly WikidataDiagnostics _diagnostics;
    private readonly EntityMemory _memory;

    public WikidataEntityFetcher(
        ResilientHttpClient httpClient,
        WikidataReconcilerOptions options,
        WikidataDiagnostics diagnostics)
    {
        _httpClient = httpClient;
        _options = options;
        _diagnostics = diagnostics;
        _memory = new EntityMemory(options.EnableResponseCaching ? options.ResponseCacheTtl : TimeSpan.Zero);
    }

    /// <summary>
    /// Fetches entities by their IDs. Automatically batches requests (max 50 per API call).
    /// Includes labels, descriptions, aliases, and claims with language fallback.
    /// </summary>
    public async Task<Dictionary<string, WikidataEntity>> FetchEntitiesAsync(
        IReadOnlyList<string> ids, string language, CancellationToken cancellationToken = default)
    {
        return await FetchInBatchesAsync(ids, language, "info|labels|descriptions|aliases|claims", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches entities with labels and aliases in ALL languages (no language filter).
    /// Used by the reconciliation pipeline so cross-language label matching works.
    /// </summary>
    public async Task<Dictionary<string, WikidataEntity>> FetchEntitiesAllLanguagesAsync(
        IReadOnlyList<string> ids, bool includeSitelinks = false, CancellationToken cancellationToken = default)
    {
        var props = includeSitelinks
            ? "info|labels|descriptions|aliases|claims|sitelinks"
            : "info|labels|descriptions|aliases|claims";
        return await FetchInBatchesAsync(ids, null, props, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches entity labels only (lightweight, for entity reference label resolution).
    /// </summary>
    public async Task<Dictionary<string, WikidataEntity>> FetchLabelsOnlyAsync(
        IReadOnlyList<string> ids, string language, CancellationToken cancellationToken = default)
    {
        return await FetchInBatchesAsync(ids, language, "labels", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches entity claims only (no terms), e.g. to check whether candidates are classes.
    /// </summary>
    public async Task<Dictionary<string, WikidataEntity>> FetchClaimsOnlyAsync(
        IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        return await FetchInBatchesAsync(ids, null, "claims", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches entities with sitelinks only (lightweight, for Wikipedia URL resolution).
    /// </summary>
    public async Task<Dictionary<string, WikidataEntity>> FetchEntitiesWithSitelinksAsync(
        IReadOnlyList<string> ids, string language, CancellationToken cancellationToken = default)
    {
        return await FetchInBatchesAsync(ids, language, "sitelinks", cancellationToken)
            .ConfigureAwait(false);
    }


    private const string FullEntityProps = "info|labels|descriptions|aliases|claims|sitelinks";

    private async Task<Dictionary<string, WikidataEntity>> FetchInBatchesAsync(
        IReadOnlyList<string> ids, string? language, string props, CancellationToken cancellationToken)
    {
        if (_options.WidenEntityFetches && (props.Contains("claims", StringComparison.Ordinal) || props == "sitelinks"))
        {
            language = null;
            props = FullEntityProps;
        }

        var result = new Dictionary<string, WikidataEntity>(StringComparer.OrdinalIgnoreCase);
        var distinctIds = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var batchSize = Math.Clamp(
            _options.WikidataRateLimit.MaxBatchSize,
            1,
            ProviderMaxIdsPerRequest);

        // Entities already fetched in this process, with the same or a smaller set of properties, are not asked for again.
        var toFetch = new List<string>(distinctIds.Count);
        foreach (var id in distinctIds)
        {
            if (_memory.TryGet(id, language, props) is { } held)
                result[id] = held;
            else
                toFetch.Add(id);
        }

        if (toFetch.Count == 0)
        {
            return result;
        }

        if (_options.EntityBatchWindow <= TimeSpan.Zero)
        {
            for (var i = 0; i < toFetch.Count; i += batchSize)
            {
                var batch = toFetch.Skip(i).Take(batchSize).ToList();
                _diagnostics.RecordBatch("wbgetentities", batch.Count);
                var batchResult = await FetchBatchAsync(batch, language, props, cancellationToken).ConfigureAwait(false);

                foreach (var kvp in batchResult)
                {
                    result[kvp.Key] = kvp.Value;
                    _memory.Remember(kvp.Key, language, props, kvp.Value);
                }
            }

            return result;
        }

        // Callers that run at the same time and want the same kind of data share requests: what they ask for is collected for a
        // short moment and fetched together, up to 50 entities per request. Wikidata is limited by requests per second, not by
        // size, so many small requests cost far more than a few full ones.
        var combined = await FetchCombinedAsync(toFetch, language, props, batchSize, cancellationToken).ConfigureAwait(false);
        foreach (var (id, entity) in combined)
        {
            result[id] = entity;
            _memory.Remember(id, language, props, entity);
        }

        return result;
    }

    private sealed class WantedEntity
    {
        public readonly TaskCompletionSource<WikidataEntity?> Source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How many callers still want this entity.</summary>
        public int Interest;

        public SharedRequest? Request;
    }

    private sealed class SharedRequest
    {
        public readonly CancellationTokenSource Cancel = new();

        /// <summary>How many of its entities still have a caller waiting; the request is cancelled when none do.</summary>
        public int Live;
    }

    private sealed class EntityWindow
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, WantedEntity> Pending = new(StringComparer.OrdinalIgnoreCase);
        public bool FlushScheduled;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Language, string Props), EntityWindow> _windows = new();

    private async Task<Dictionary<string, WikidataEntity>> FetchCombinedAsync(
        List<string> ids, string? language, string props, int batchSize, CancellationToken cancellationToken)
    {
        var window = _windows.GetOrAdd((language ?? "*", props), static _ => new EntityWindow());
        var waits = new List<(string Id, WantedEntity Wanted)>(ids.Count);
        List<List<KeyValuePair<string, WantedEntity>>> full;
        var scheduleFlush = false;

        lock (window.Gate)
        {
            foreach (var id in ids)
            {
                if (!window.Pending.TryGetValue(id, out var wanted))
                {
                    wanted = new WantedEntity();
                    window.Pending[id] = wanted;
                }

                wanted.Interest++;
                waits.Add((id, wanted));
            }

            full = TakeBatches(window, batchSize, onlyFull: true);
            if (window.Pending.Count > 0 && !window.FlushScheduled)
            {
                window.FlushScheduled = true;
                scheduleFlush = true;
            }
        }

        foreach (var batch in full)
        {
            _ = RunSharedRequestAsync(batch, language, props);
        }

        if (scheduleFlush)
        {
            _ = FlushAfterWindowAsync(window, language, props, batchSize);
        }

        try
        {
            var found = await Task.WhenAll(waits.Select(wait => wait.Wanted.Source.Task))
                .WaitAsync(cancellationToken).ConfigureAwait(false);

            var result = new Dictionary<string, WikidataEntity>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < waits.Count; index++)
            {
                if (found[index] is { } entity)
                {
                    result[waits[index].Id] = entity;
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // This caller stops waiting, so it withdraws its interest; a request nobody waits for any more is cancelled.
            foreach (var (id, wanted) in waits)
            {
                Withdraw(window, id, wanted);
            }

            throw;
        }
    }

    private static void Withdraw(EntityWindow window, string id, WantedEntity wanted)
    {
        lock (window.Gate)
        {
            if (--wanted.Interest > 0)
            {
                return;
            }

            if (wanted.Request is null)
            {
                // Not sent yet: nobody wants it, so it is not asked for.
                if (window.Pending.TryGetValue(id, out var pending) && ReferenceEquals(pending, wanted))
                {
                    window.Pending.Remove(id);
                }

                wanted.Source.TrySetCanceled();
                return;
            }

            if (--wanted.Request.Live == 0)
            {
                wanted.Request.Cancel.Cancel();
            }
        }
    }

    /// <summary>Takes pending entities out of the window as requests of up to <paramref name="batchSize"/> (call with the gate held).</summary>
    private static List<List<KeyValuePair<string, WantedEntity>>> TakeBatches(EntityWindow window, int batchSize, bool onlyFull)
    {
        var batches = new List<List<KeyValuePair<string, WantedEntity>>>();
        while (window.Pending.Count >= (onlyFull ? batchSize : 1))
        {
            var batch = window.Pending.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Take(batchSize).ToList();
            var request = new SharedRequest { Live = batch.Count };
            foreach (var pair in batch)
            {
                window.Pending.Remove(pair.Key);
                pair.Value.Request = request;
            }

            batches.Add(batch);
        }

        return batches;
    }

    private async Task FlushAfterWindowAsync(EntityWindow window, string? language, string props, int batchSize)
    {
        await Task.Delay(_options.EntityBatchWindow).ConfigureAwait(false);

        List<List<KeyValuePair<string, WantedEntity>>> batches;
        lock (window.Gate)
        {
            window.FlushScheduled = false;
            batches = TakeBatches(window, batchSize, onlyFull: false);
        }

        foreach (var batch in batches)
        {
            _ = RunSharedRequestAsync(batch, language, props);
        }
    }

    private async Task RunSharedRequestAsync(List<KeyValuePair<string, WantedEntity>> batch, string? language, string props)
    {
        var request = batch[0].Value.Request!;
        try
        {
            _diagnostics.RecordBatch("wbgetentities", batch.Count);
            var fetched = await FetchBatchAsync(batch.Select(pair => pair.Key).ToList(), language, props, request.Cancel.Token)
                .ConfigureAwait(false);
            var byId = new Dictionary<string, WikidataEntity>(fetched, StringComparer.OrdinalIgnoreCase);
            foreach (var (id, wanted) in batch)
            {
                wanted.Source.TrySetResult(byId.GetValueOrDefault(id));
            }
        }
        catch (Exception ex)
        {
            foreach (var (_, wanted) in batch)
            {
                wanted.Source.TrySetException(ex);
            }
        }
    }

    private async Task<Dictionary<string, WikidataEntity>> FetchBatchAsync(
        List<string> ids, string? language, string props, CancellationToken cancellationToken)
    {
        var idsParam = string.Join('|', ids);
        var url = $"{_options.ApiEndpoint}?action=wbgetentities&ids={Uri.EscapeDataString(idsParam)}" +
                  $"&format=json&props={Uri.EscapeDataString(props)}";

        if (language is not null)
        {
            var languageParam = LanguageFallback.BuildLanguageParam(language);
            url += $"&languages={Uri.EscapeDataString(languageParam)}";
        }

        string json;
        try
        {
            json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (WikidataProviderException ex) when (
            ex.Kind == WikidataFailureKind.NotFound &&
            ex.MissingEntityId is { } missing &&
            ids.RemoveAll(id => string.Equals(id, missing, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            // Wikibase fails the whole request when any ID does not exist. Drop the named ID and
            // fetch the rest, so one stale reference cannot break a batch of up to 50 entities.
            // A missing entity is simply absent from the result, as for "missing" entries.
            return ids.Count == 0
                ? new Dictionary<string, WikidataEntity>()
                : await FetchBatchAsync(ids, language, props, cancellationToken).ConfigureAwait(false);
        }

        var response = ProviderJson.Deserialize(json, WikidataJsonContext.Default.WbGetEntitiesResponse, "wbgetentities");

        return response?.Entities ?? new Dictionary<string, WikidataEntity>();
    }

    /// <summary>
    /// Extracts all labels and aliases for a given entity in the requested language.
    /// Uses language fallback chain for labels.
    /// </summary>
    public static List<string> GetAllLabels(WikidataEntity entity, string language)
    {
        var labels = new List<string>();

        // Primary label with fallback
        if (LanguageFallback.TryGetValue(entity.Labels, language, out var label))
            labels.Add(label);

        // Aliases in requested language (no fallback — aliases are language-specific)
        if (entity.Aliases?.TryGetValue(language, out var aliases) == true)
        {
            foreach (var alias in aliases)
            {
                if (!string.IsNullOrEmpty(alias.Value))
                    labels.Add(alias.Value);
            }
        }

        return labels;
    }

    /// <summary>
    /// Extracts all labels and aliases across ALL languages for cross-language scoring.
    /// Returns deduplicated label strings from every available language.
    /// </summary>
    public static List<string> GetAllLabelsAllLanguages(WikidataEntity entity)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var labels = new List<string>();

        // All labels across every language
        if (entity.Labels is not null)
        {
            foreach (var lv in entity.Labels.Values)
            {
                if (!string.IsNullOrEmpty(lv.Value) && seen.Add(lv.Value))
                    labels.Add(lv.Value);
            }
        }

        // All aliases across every language
        if (entity.Aliases is not null)
        {
            foreach (var aliasList in entity.Aliases.Values)
            {
                foreach (var alias in aliasList)
                {
                    if (!string.IsNullOrEmpty(alias.Value) && seen.Add(alias.Value))
                        labels.Add(alias.Value);
                }
            }
        }

        return labels;
    }

    /// <summary>
    /// Extracts all labels, aliases, and sitelink titles across ALL languages.
    /// Sitelink titles are often more human-friendly than formal labels.
    /// </summary>
    public static List<string> GetAllLabelsWithSitelinks(WikidataEntity entity)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var labels = new List<string>();

        // Start with all regular labels and aliases
        foreach (var label in GetAllLabelsAllLanguages(entity))
        {
            if (seen.Add(label))
                labels.Add(label);
        }

        // Add sitelink titles
        if (entity.Sitelinks is not null)
        {
            foreach (var sitelink in entity.Sitelinks.Values)
            {
                if (!string.IsNullOrEmpty(sitelink.Title) && seen.Add(sitelink.Title))
                    labels.Add(sitelink.Title);
            }
        }

        return labels;
    }

    /// <summary>
    /// Extracts claim values for a property, respecting Wikidata rank hierarchy.
    /// </summary>
    public static List<DataValue> GetClaimValues(WikidataEntity entity, string propertyId)
    {
        if (entity.Claims?.TryGetValue(propertyId, out var claims) != true || claims!.Count == 0)
            return [];

        var validClaims = claims
            .Where(c => c.Rank != "deprecated" && c.MainSnak?.SnakType == "value" && c.MainSnak.DataValue != null)
            .ToList();

        var preferred = validClaims.Where(c => c.Rank == "preferred").ToList();
        var source = preferred.Count > 0 ? preferred : validClaims.Where(c => c.Rank == "normal").ToList();

        return source.Select(c => c.MainSnak!.DataValue!).ToList();
    }

    /// <summary>
    /// Extracts P31 (instance of) type QIDs for an entity.
    /// </summary>
    public static List<string> GetTypeIds(WikidataEntity entity, string typePropertyId)
    {
        if (entity.Claims?.TryGetValue(typePropertyId, out var claims) != true)
            return [];

        var types = new List<string>();
        foreach (var claim in claims!)
        {
            if (claim.MainSnak?.SnakType == "value" && claim.MainSnak.DataValue?.Value is JsonElement element)
            {
                if (element.TryGetProperty("id", out var idProp))
                    types.Add(idProp.GetString() ?? "");
            }
        }

        return types.Where(t => !string.IsNullOrEmpty(t)).ToList();
    }
}
