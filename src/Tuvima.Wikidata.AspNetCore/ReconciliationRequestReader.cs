using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Tuvima.Wikidata.AspNetCore;

/// <summary>Validate the complete client payload before starting any provider work.</summary>
internal static class ReconciliationRequestReader
{
    internal sealed record ParsedQueries(bool IsBatch, Dictionary<string, W3cQuery> Queries);

    internal sealed record ParsedExtend(W3cExtendRequest Request);

    /// <summary>Reads a reconciliation ('query'/'queries') or data-extension ('extend') POST.</summary>
    public static async Task<(ParsedQueries? Queries, ParsedExtend? Extend, IResult? Error)> ReadPostAsync(
        HttpRequest request, ReconciliationServiceOptions options, CancellationToken cancellationToken)
    {
        var (form, error) = await ReadFormAsync(request, options, cancellationToken);
        if (error is not null)
            return (null, null, error);

        if (form!.ContainsKey("extend") && !form.ContainsKey("query") && !form.ContainsKey("queries"))
        {
            try
            {
                var (extend, extendError) = ParseExtend(form, options);
                return (null, extend, extendError);
            }
            catch (JsonException)
            {
                return (null, null, Error(400, "The extend parameter contains malformed JSON or invalid field types."));
            }
        }

        try
        {
            var (queries, queryError) = ParseQueries(form);
            if (queryError is not null)
                return (null, null, queryError);
            if (queries!.Queries.Count > options.MaxBatchSize)
                return (null, null, Error(400, "The batch exceeds MaxBatchSize."));

            foreach (var (key, query) in queries.Queries)
            {
                if (queries.IsBatch && string.IsNullOrWhiteSpace(key))
                    return (null, null, Error(400, "Batch keys must not be blank."));
                var issue = Validate(query, options);
                if (issue is not null) return (null, null, Error(400, issue));
            }
            return (queries, null, null);
        }
        catch (JsonException)
        {
            return (null, null, Error(400, "The query parameter contains malformed JSON or invalid field types."));
        }
    }

    private static async Task<(IFormCollection? Form, IResult? Error)> ReadFormAsync(
        HttpRequest request, ReconciliationServiceOptions options, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return (null, Error(415, "Use application/x-www-form-urlencoded or multipart/form-data."));
        if (request.ContentLength > options.MaxRequestBodyBytes)
            return (null, Error(413, "The request body exceeds the configured size limit."));

        try
        {
            // Bound bodies without Content-Length too, before form buffering/deserialization.
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            var originalBody = request.Body;
            while (true)
            {
                var remaining = (long)options.MaxRequestBodyBytes - body.Length + 1;
                var count = await originalBody.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (count == 0) break;
                if (body.Length + count > options.MaxRequestBodyBytes)
                    return (null, Error(413, "The request body exceeds the configured size limit."));
                body.Write(buffer, 0, count);
            }
            body.Position = 0;
            IFormCollection form;
            request.Body = body;
            try
            {
                form = await request.ReadFormAsync(new FormOptions
                {
                    ValueLengthLimit = options.MaxRequestBodyBytes,
                    MultipartBodyLengthLimit = options.MaxRequestBodyBytes
                }, cancellationToken);
            }
            finally { request.Body = originalBody; }

            if (form.Files.Count > 0)
                return (null, Error(400, "File uploads are not supported."));
            return (form, null);
        }
        catch (InvalidDataException)
        {
            return (null, Error(400, "The request contains invalid form data."));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode is 400 or 413)
        {
            return (null, Error(ex.StatusCode, "The request body is invalid or exceeds the server limit."));
        }
    }

    private static (ParsedQueries? Payload, IResult? Error) ParseQueries(IFormCollection form)
    {
        var hasBatch = form.TryGetValue("queries", out var batch);
        var hasSingle = form.TryGetValue("query", out var single);
        if (hasBatch == hasSingle || (hasBatch ? batch.Count : single.Count) != 1)
            return (null, Error(400, "Supply exactly one 'query' or 'queries' form value."));

        var json = hasBatch ? batch[0] : single[0];
        if (string.IsNullOrWhiteSpace(json))
            return (null, Error(400, "The query parameter must contain a JSON object."));

        var queries = hasBatch
            ? JsonSerializer.Deserialize(json, W3cJsonContext.Default.DictionaryStringW3cQuery)
            : new Dictionary<string, W3cQuery> { [""] = JsonSerializer.Deserialize(json, W3cJsonContext.Default.W3cQuery)! };
        if (queries is null || queries.Count == 0)
            return (null, Error(400, "At least one query is required."));
        return (new(hasBatch, queries), null);
    }

    private static (ParsedExtend? Payload, IResult? Error) ParseExtend(IFormCollection form, ReconciliationServiceOptions options)
    {
        var values = form["extend"];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
            return (null, Error(400, "Supply exactly one 'extend' JSON value."));

        var extend = JsonSerializer.Deserialize(values[0]!, W3cJsonContext.Default.W3cExtendRequest);
        if (extend?.Ids is not { Count: > 0 } || extend.Properties is not { Count: > 0 })
            return (null, Error(400, "The extend request needs nonempty 'ids' and 'properties' arrays."));
        if (extend.Ids.Count > options.MaxBatchSize)
            return (null, Error(400, "The extend request exceeds MaxBatchSize ids."));
        if (extend.Properties.Count > options.MaxPropertiesPerQuery)
            return (null, Error(400, "The extend request exceeds MaxPropertiesPerQuery."));
        if (extend.Ids.Any(id => !DataExtension.IsEntityId(id, 'Q')))
            return (null, Error(400, "Each id must be a Wikidata item ID such as Q42."));
        if (extend.Properties.Any(p => p is null || !DataExtension.IsEntityId(p.Id, 'P')))
            return (null, Error(400, "Each property must have an 'id' such as P569."));
        return (new(extend), null);
    }

    private static string? Validate(W3cQuery? query, ReconciliationServiceOptions options)
    {
        if (query is null || string.IsNullOrWhiteSpace(query.Query))
            return "Each query must be a non-null object with nonblank 'query' text.";
        if (query.Query.Length > options.MaxQueryLength || query.Type?.Length > options.MaxQueryLength)
            return "Query text or type exceeds MaxQueryLength.";
        if (query.Limit < 0 || query.Limit > options.MaxResultLimit)
            return "The candidate limit must be between zero and MaxResultLimit; zero uses the default.";
        if (query.Properties?.Count > options.MaxPropertiesPerQuery)
            return "The query exceeds MaxPropertiesPerQuery.";
        if (query.Properties is not null)
            foreach (var property in query.Properties)
                if (property is null || string.IsNullOrWhiteSpace(property.Pid) || string.IsNullOrWhiteSpace(property.V) ||
                    property.Pid.Length > options.MaxQueryLength || property.V.Length > options.MaxQueryLength)
                    return "Each property must have nonblank 'pid' and 'v' strings within MaxQueryLength.";
        return null;
    }

    private static IResult Error(int status, string detail) => Results.Problem(statusCode: status, detail: detail);
}
