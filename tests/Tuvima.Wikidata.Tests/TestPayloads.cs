using System.Text.Json;

namespace Tuvima.Wikidata.Tests;

internal static class TestPayloads
{
    public sealed record ClaimSpec(
        string PropertyId,
        string DataType,
        object DataValue,
        string Rank,
        Dictionary<string, object>? Qualifiers = null);

    public static WikidataReconciler CreateReconciler(TestHttpMessageHandler handler)
    {
        return new WikidataReconciler(CreateHttpClient(handler), new WikidataReconcilerOptions
        {
            UserAgent = "Tuvima.Wikidata.Tests/2.6 (https://github.com/Tuvima/wikidata)",
            EnableResponseCaching = false,
            // These fixtures answer each request by its exact id list, so callers must not be combined.
            EntityBatchWindow = TimeSpan.Zero,
            WikidataRateLimit = ProviderRateLimitOptions.Unthrottled,
            WikipediaRateLimit = ProviderRateLimitOptions.Unthrottled,
            CommonsRateLimit = ProviderRateLimitOptions.Unthrottled,
            DefaultRateLimit = ProviderRateLimitOptions.Unthrottled
        });
    }

    public static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Tuvima.Wikidata.Tests/2.6 (https://github.com/Tuvima/wikidata)");
        return client;
    }

    public static string SearchResponse(params (string Id, string Label)[] results)
    {
        return JsonSerializer.Serialize(new
        {
            success = 1,
            search = results.Select(r => new { id = r.Id, label = r.Label }).ToList()
        });
    }

    public static string QueryResponse(params string[] titles)
    {
        return JsonSerializer.Serialize(new
        {
            query = new
            {
                search = titles.Select(title => new { title }).ToList()
            }
        });
    }

    public static string RecentChangesResponse(
        IEnumerable<object> entries,
        string? continueToken = null)
    {
        return JsonSerializer.Serialize(new
        {
            query = new
            {
                recentchanges = entries.ToList()
            },
            @continue = continueToken is null ? null : new
            {
                rccontinue = continueToken
            }
        });
    }

    public static string EntityResponse(params Dictionary<string, object?>[] entities)
    {
        return JsonSerializer.Serialize(new
        {
            success = 1,
            entities = entities.ToDictionary(
                entity => (string)entity["id"]!,
                entity => entity)
        });
    }

    public static Dictionary<string, object?> Entity(
        string id,
        string label,
        Dictionary<string, object>? claims = null,
        Dictionary<string, object>? sitelinks = null)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["type"] = "item",
            ["labels"] = Labels(("en", label)),
            ["claims"] = claims,
            ["sitelinks"] = sitelinks
        };
    }

    public static Dictionary<string, object> Labels(params (string Language, string Value)[] values)
    {
        return values.ToDictionary(
            value => value.Language,
            value => (object)new
            {
                language = value.Language,
                value = value.Value
            });
    }

    public static Dictionary<string, object> Sitelinks(params (string Site, string Title)[] values)
    {
        return values.ToDictionary(
            value => value.Site,
            value => (object)new
            {
                site = value.Site,
                title = value.Title
            });
    }

    public static Dictionary<string, object> Claims(
        params (string PropertyId, string DataType, object DataValue, string Rank)[] values)
    {
        return values
            .GroupBy(value => value.PropertyId)
            .ToDictionary(
                group => group.Key,
                group => (object)group.Select(value => new
                {
                    mainsnak = new
                    {
                        snaktype = "value",
                        property = value.PropertyId,
                        datavalue = value.DataValue,
                        datatype = value.DataType
                    },
                    rank = value.Rank
                }).ToList());
    }

    public static Dictionary<string, object> ClaimsWithQualifiers(params ClaimSpec[] values)
    {
        return values
            .GroupBy(value => value.PropertyId)
            .ToDictionary(
                group => group.Key,
                group => (object)group.Select(value => new
                {
                    mainsnak = new
                    {
                        snaktype = "value",
                        property = value.PropertyId,
                        datavalue = value.DataValue,
                        datatype = value.DataType
                    },
                    rank = value.Rank,
                    qualifiers = value.Qualifiers,
                    qualifiers_order = value.Qualifiers?.Keys.ToList()
                }).ToList());
    }

    public static ClaimSpec Claim(
        string propertyId,
        string dataType,
        object dataValue,
        string rank = "normal",
        Dictionary<string, object>? qualifiers = null)
        => new(propertyId, dataType, dataValue, rank, qualifiers);

    public static Dictionary<string, object> Qualifiers(
        params (string PropertyId, string DataType, object DataValue)[] values)
    {
        return values
            .GroupBy(value => value.PropertyId)
            .ToDictionary(
                group => group.Key,
                group => (object)group.Select(value => new
                {
                    snaktype = "value",
                    property = value.PropertyId,
                    datavalue = value.DataValue,
                    datatype = value.DataType
                }).ToList());
    }

    public static object ItemDataValue(string qid)
        => new
        {
            value = new { id = qid },
            type = "wikibase-entityid"
        };

    public static object StringDataValue(string value)
        => new
        {
            value,
            type = "string"
        };

    public static object TimeDataValue(string time, int precision = 11)
        => new
        {
            value = new
            {
                time,
                precision,
                timezone = 0,
                before = 0,
                after = 0,
                calendarmodel = "http://www.wikidata.org/entity/Q1985727"
            },
            type = "time"
        };
}
