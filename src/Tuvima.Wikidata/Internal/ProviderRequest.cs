using System.Text;

namespace Tuvima.Wikidata.Internal;

internal sealed class ProviderRequest
{
    private static readonly HashSet<string> SortedPipeParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "ids",
        "titles",
        "props"
    };

    private ProviderRequest(Uri uri, WikidataResponseCacheKey cacheKey)
    {
        Uri = uri;
        CacheKey = cacheKey;
    }

    public Uri Uri { get; }

    public WikidataResponseCacheKey CacheKey { get; }

    public string Host => CacheKey.Host;

    public string Endpoint => CacheKey.Endpoint;

    public static ProviderRequest Create(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var query = ParseQuery(uri.Query);
        var endpoint = GetEndpoint(uri, query);
        var key = BuildCanonicalKey(uri, query);
        return new ProviderRequest(uri, new WikidataResponseCacheKey(uri.Host, endpoint, key));
    }

    public bool IsCacheable()
    {
        if (Host.Equals("commons.wikimedia.org", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Host.EndsWith(".wikipedia.org", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.AbsolutePath.Contains("/api/rest_v1/page/summary/", StringComparison.OrdinalIgnoreCase))
                return true;

            var query = ParseQuery(Uri.Query);
            if (!query.TryGetValue("action", out var wikipediaActions))
                return false;

            // The table of contents of an article (action=parse&prop=tocdata) only changes when the article is edited.
            if (wikipediaActions.Contains("parse", StringComparer.OrdinalIgnoreCase) &&
                query.TryGetValue("prop", out var parseProps) &&
                parseProps.All(prop => prop.Equals("tocdata", StringComparison.OrdinalIgnoreCase)))
                return true;

            return wikipediaActions.Contains("query", StringComparer.OrdinalIgnoreCase) &&
                   query.TryGetValue("prop", out var props) &&
                   props.Any(prop =>
                       prop.Contains("extracts", StringComparison.OrdinalIgnoreCase) ||
                       prop.Contains("pageimages", StringComparison.OrdinalIgnoreCase) ||
                       prop.Contains("info", StringComparison.OrdinalIgnoreCase));
        }

        if (Host.Equals("www.wikidata.org", StringComparison.OrdinalIgnoreCase) ||
            Uri.AbsolutePath.EndsWith("/w/api.php", StringComparison.OrdinalIgnoreCase))
        {
            var query = ParseQuery(Uri.Query);
            if (!query.TryGetValue("action", out var actions))
                return false;

            if (actions.Contains("wbgetentities", StringComparer.OrdinalIgnoreCase) ||
                actions.Contains("wbsearchentities", StringComparer.OrdinalIgnoreCase))
                return true;

            // Full-text search (action=query&list=search), including the haswbstatement type and external-ID searches.
            // A search answer is as stable as the entity data it points at, so repeated searches for the same text
            // (the same person credited on many titles) cost one request, not one each. Malformed or error answers
            // are never cached (ProviderJson), and answers expire with ResponseCacheTtl.
            return actions.Contains("query", StringComparer.OrdinalIgnoreCase) &&
                   query.TryGetValue("list", out var lists) &&
                   lists.Contains("search", StringComparer.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string GetEndpoint(Uri uri, Dictionary<string, List<string>> query)
    {
        if (uri.AbsolutePath.Contains("/api/rest_v1/page/summary/", StringComparison.OrdinalIgnoreCase))
            return "rest.summary";

        if (!query.TryGetValue("action", out var actions) || actions.Count == 0)
            return uri.AbsolutePath.Trim('/').Replace('/', '.');

        var action = actions[0];
        if (!action.Equals("query", StringComparison.OrdinalIgnoreCase))
            return action;

        if (query.TryGetValue("list", out var lists) && lists.Count > 0)
            return $"query.{lists[0]}";

        if (query.TryGetValue("prop", out var props) && props.Count > 0)
            return $"query.{props[0].Replace('|', '+')}";

        return "query";
    }

    private static string BuildCanonicalKey(Uri uri, Dictionary<string, List<string>> query)
    {
        var builder = new StringBuilder();
        builder.Append(uri.Scheme.ToLowerInvariant())
            .Append("://")
            .Append(uri.Host.ToLowerInvariant())
            .Append(uri.AbsolutePath);

        if (query.Count == 0)
            return builder.ToString();

        builder.Append('?');
        var first = true;

        foreach (var (key, values) in query.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            var normalizedValues = values.Select(value => NormalizeValue(key, value)).ToList();
            if (!key.Equals("languages", StringComparison.OrdinalIgnoreCase))
                normalizedValues.Sort(StringComparer.Ordinal);

            foreach (var value in normalizedValues)
            {
                if (!first)
                    builder.Append('&');
                first = false;

                builder.Append(Uri.EscapeDataString(key.ToLowerInvariant()))
                    .Append('=')
                    .Append(Uri.EscapeDataString(value));
            }
        }

        return builder.ToString();
    }

    private static string NormalizeValue(string key, string value)
    {
        if (!SortedPipeParameters.Contains(key) || !value.Contains('|'))
            return value;

        var parts = value
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(part => part, StringComparer.OrdinalIgnoreCase);

        return string.Join('|', parts);
    }

    private static Dictionary<string, List<string>> ParseQuery(string query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
            return result;

        var trimmed = query[0] == '?' ? query[1..] : query;
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var rawKey = separator >= 0 ? pair[..separator] : pair;
            var rawValue = separator >= 0 ? pair[(separator + 1)..] : "";
            var key = Uri.UnescapeDataString(rawKey.Replace("+", " "));
            var value = Uri.UnescapeDataString(rawValue.Replace("+", " "));

            if (!result.TryGetValue(key, out var values))
            {
                values = [];
                result[key] = values;
            }
            values.Add(value);
        }

        return result;
    }
}
