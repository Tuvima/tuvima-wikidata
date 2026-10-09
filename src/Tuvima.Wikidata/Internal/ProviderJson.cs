using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tuvima.Wikidata.Internal.Json;

namespace Tuvima.Wikidata.Internal;

internal static class ProviderJson
{
    public static bool IsValidCachedResponse(string json, string endpoint)
    {
        try { return ValidateResponse(json, endpoint) is null; }
        catch (WikidataProviderException) { return false; }
    }

    /// <summary>Checks provider errors and known response shapes before a body can enter the cache.</summary>
    public static ProviderError? ValidateResponse(string json, string endpoint)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("Expected a provider response object.");

            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                return ReadError(error);
            if (root.TryGetProperty("errors", out var errors))
            {
                if (errors.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Expected an API errors array.");
                ProviderError? selected = null;
                foreach (var entry in errors.EnumerateArray())
                {
                    var candidate = ReadError(entry);
                    // A permanent rejection must not be hidden by a retryable error.
                    if (selected is null || !candidate.Retryable)
                        selected = candidate;
                }
                if (selected is not null) return selected;
            }

            // Source-generated contracts also reject valid JSON with invalid field types.
            // Keep validation at the cache boundary so those bodies cannot poison later calls.
            var context = WikidataJsonContext.Default;
            switch (endpoint)
            {
                case "wbgetentities": JsonSerializer.Deserialize(json, context.WbGetEntitiesResponse); break;
                case "wbsearchentities": JsonSerializer.Deserialize(json, context.WbSearchEntitiesResponse); break;
                case "query.search": JsonSerializer.Deserialize(json, context.QuerySearchResponse); break;
                case "query.recentchanges": JsonSerializer.Deserialize(json, context.RecentChangesResponse); break;
                case "query.revisions": JsonSerializer.Deserialize(json, context.RevisionQueryResponse); break;
                case "parse": JsonSerializer.Deserialize(json, context.ParseResponse); break;
                case "rest.summary": JsonSerializer.Deserialize(json, context.WikipediaSummaryResponse); break;
                default:
                    if (endpoint.StartsWith("query.", StringComparison.Ordinal) && endpoint.Contains("extracts", StringComparison.Ordinal))
                        JsonSerializer.Deserialize(json, context.WikipediaSummaryBatchResponse);
                    break;
            }
            return null;
        }
        catch (JsonException ex)
        {
            throw new WikidataProviderException(WikidataFailureKind.MalformedResponse,
                $"The provider returned malformed JSON or an invalid response shape for {endpoint}.", innerException: ex);
        }
    }

    private static ProviderError ReadError(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("code", out var codeValue) ||
            codeValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(codeValue.GetString()))
            throw new JsonException("Expected a provider error code.");
        var code = codeValue.GetString()!;
        // wbgetentities names the offending ID; callers use it to retry a batch without that ID.
        var entityId = error.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
            ? idValue.GetString()
            : null;
        return code.ToLowerInvariant() switch
        {
            "maxlag" or "ratelimited" => new(code, WikidataFailureKind.RateLimited, true),
            "readonly" or "internal_api_error_dbconnectionerror" or "internal_api_error_dbquerytimeouterror"
                => new(code, WikidataFailureKind.TransientNetworkFailure, true),
            "missingtitle" or "nosuchentity" or "no-such-entity" or "nosuchsection"
                => new(code, WikidataFailureKind.NotFound, false, entityId),
            _ => new(code, WikidataFailureKind.ProviderRejected, false)
        };
    }

    public static T? Deserialize<T>(string json, JsonTypeInfo<T> jsonTypeInfo, string endpoint)
    {
        try
        {
            return JsonSerializer.Deserialize(json, jsonTypeInfo);
        }
        catch (JsonException ex)
        {
            throw new WikidataProviderException(
                WikidataFailureKind.MalformedResponse,
                $"The provider returned malformed JSON for {endpoint}.",
                innerException: ex);
        }
    }
}

internal sealed record ProviderError(string Code, WikidataFailureKind Kind, bool Retryable, string? EntityId = null);
