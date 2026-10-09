using System.Net;
using System.Text.Json;

namespace Tuvima.Wikidata;

/// <summary>
/// Abstract base class for HTTP-level response caching. Override <see cref="GetCachedResponseAsync"/>
/// and <see cref="CacheResponseAsync"/> with your cache backend (MemoryCache, Redis, SQLite, etc.).
/// <para>
/// Usage:
/// <code>
/// var handler = new MyCachingHandler(myCache) { InnerHandler = new HttpClientHandler() };
/// var httpClient = new HttpClient(handler);
/// var reconciler = new WikidataReconciler(httpClient, options);
/// </code>
/// </para>
/// Use <see cref="WikidataReconciler.GetRevisionIdsAsync"/> for lightweight staleness checks
/// to implement cache invalidation.
/// <para>
/// Prefer <see cref="WikidataReconcilerOptions.ResponseCache"/>: it caches only validated, cacheable
/// provider responses and is keyed canonically. This handler sits below the retry pipeline and only
/// stores successful JSON bodies that are not MediaWiki error envelopes.
/// </para>
/// </summary>
[Obsolete("Use WikidataReconcilerOptions.ResponseCache with an IWikidataResponseCache implementation instead.")]
public abstract class CachingDelegatingHandler : DelegatingHandler
{
    /// <summary>
    /// Attempts to retrieve a cached response for the given request URI.
    /// Return null if the response is not cached.
    /// </summary>
    /// <param name="requestUri">The full request URI to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached response content as a string, or null if not cached.</returns>
    protected abstract Task<string?> GetCachedResponseAsync(string requestUri, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a response in the cache for the given request URI.
    /// </summary>
    /// <param name="requestUri">The full request URI as the cache key.</param>
    /// <param name="responseContent">The response body to cache.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected abstract Task CacheResponseAsync(string requestUri, string responseContent, CancellationToken cancellationToken);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Only cache GET requests
        if (request.Method != HttpMethod.Get || request.RequestUri is null)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var uri = request.RequestUri.ToString();

        var cached = await GetCachedResponseAsync(uri, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(cached, System.Text.Encoding.UTF8, "application/json")
            };
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            // MediaWiki reports maxlag and other failures as HTTP 200 error envelopes. Caching one
            // would replay the failure to every retry, so only cache bodies that look like real data.
            if (IsCacheableBody(content))
                await CacheResponseAsync(uri, content, cancellationToken).ConfigureAwait(false);

            // Replace the response content since ReadAsStringAsync consumed it
            response.Content = new StringContent(content, System.Text.Encoding.UTF8,
                response.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return response;
    }

    internal static bool IsCacheableBody(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return root.ValueKind == JsonValueKind.Array;
            return !root.TryGetProperty("error", out _) && !root.TryGetProperty("errors", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
