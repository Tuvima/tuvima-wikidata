using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Tuvima.Wikidata.AspNetCore;

/// <summary>
/// Extension methods to map W3C Reconciliation Service API endpoints.
/// Compatible with OpenRefine, Google Sheets reconciliation, and any W3C-compatible client.
/// </summary>
public static class ReconciliationEndpoints
{
    /// <summary>
    /// Maps the W3C Reconciliation Service API endpoints at the specified path prefix.
    /// Includes reconciliation, data extension (property proposals and values), suggest
    /// (entity/property/type), and preview endpoints.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pathPrefix">The URL prefix (e.g., "/api/reconcile"). Default is "/reconcile".</param>
    /// <param name="configure">Optional configuration for the service manifest and request limits.</param>
    public static IEndpointRouteBuilder MapReconciliation(
        this IEndpointRouteBuilder endpoints,
        string pathPrefix = "/reconcile",
        Action<ReconciliationServiceOptions>? configure = null)
    {
        var serviceOptions = new ReconciliationServiceOptions();
        configure?.Invoke(serviceOptions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serviceOptions.MaxBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serviceOptions.MaxQueryLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serviceOptions.MaxResultLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serviceOptions.MaxPropertiesPerQuery);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serviceOptions.MaxRequestBodyBytes);

        var prefix = pathPrefix.TrimEnd('/');

        // GET — returns service manifest
        endpoints.MapGet(prefix, (HttpContext ctx) =>
        {
            var manifest = BuildManifest(serviceOptions, ctx.Request, prefix);
            return Results.Json(manifest, W3cJsonContext.Default.ServiceManifest);
        });

        // POST — reconciliation queries or data extension (with Accept-Language support)
        endpoints.MapPost(prefix, async (HttpContext ctx, WikidataReconciler reconciler) =>
        {
            var acceptLanguage = GetAcceptLanguage(ctx);
            var (payload, extend, error) = await ReconciliationRequestReader.ReadPostAsync(ctx.Request, serviceOptions, ctx.RequestAborted);
            if (error is not null) return error;
            if (extend is not null)
            {
                var extended = await DataExtension.ExtendAsync(reconciler, extend.Request, acceptLanguage, ctx.RequestAborted);
                return Results.Json(extended, W3cJsonContext.Default.W3cExtendResponse);
            }
            var entries = payload!.Queries.ToArray();
            var requests = entries.Select(entry => MapToRequest(entry.Value, acceptLanguage,
                Math.Min(5, serviceOptions.MaxResultLimit))).ToArray();
            var reconciled = await reconciler.Reconcile.ReconcileBatchAsync(requests, ctx.RequestAborted);
            if (!payload.IsBatch)
                return Results.Json(new W3cQueryResponse { Result = reconciled[0].Select(MapToCandidate).ToList() },
                    W3cJsonContext.Default.W3cQueryResponse);

            var results = new Dictionary<string, List<W3cCandidate>>(StringComparer.Ordinal);
            for (var i = 0; i < entries.Length; i++)
                results.Add(entries[i].Key, reconciled[i].Select(MapToCandidate).ToList());
            return Results.Json(results, W3cJsonContext.Default.DictionaryStringListW3cCandidate);
        });

        // ─── Data Extension ───────────────────────────────────────────

        // GET /propose_properties?type=Q5&limit=10
        endpoints.MapGet($"{prefix}/propose_properties", async (HttpContext ctx, WikidataReconciler reconciler) =>
        {
            var type = ctx.Request.Query["type"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(type))
                type = null;
            else if (!DataExtension.IsEntityId(type, 'Q'))
                return Results.Problem(statusCode: 400, detail: "The 'type' parameter must be a Wikidata item ID such as Q5.");

            var limitParam = ctx.Request.Query["limit"].FirstOrDefault();
            var limit = 10;
            if (!string.IsNullOrWhiteSpace(limitParam) &&
                (!int.TryParse(limitParam, out limit) || limit <= 0 || limit > serviceOptions.MaxResultLimit))
                return Results.Problem(statusCode: 400, detail: "The 'limit' parameter must be between 1 and MaxResultLimit.");

            var proposals = await DataExtension.ProposeAsync(
                reconciler, type?.ToUpperInvariant(), limit, GetAcceptLanguage(ctx), ctx.RequestAborted);
            return Results.Json(proposals, W3cJsonContext.Default.W3cProposeResponse);
        });

        // ─── Suggest Endpoints ────────────────────────────────────────

        // GET /suggest/entity?prefix=...
        endpoints.MapGet($"{prefix}/suggest/entity", async (HttpContext ctx, WikidataReconciler reconciler) =>
        {
            var prefixParam = ctx.Request.Query["prefix"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(prefixParam))
                return Results.Json(new W3cSuggestResponse(), W3cJsonContext.Default.W3cSuggestResponse);

            var language = GetAcceptLanguage(ctx);
            if (prefixParam.Length > serviceOptions.MaxQueryLength)
                return Results.Problem(statusCode: 400, detail: "The prefix exceeds MaxQueryLength.");
            var results = await reconciler.SuggestAsync(prefixParam, 7, language, ctx.RequestAborted);

            var response = new W3cSuggestResponse
            {
                Result = results.Select(r => new W3cSuggestItem
                {
                    Id = r.Id,
                    Name = r.Name,
                    Description = r.Description ?? ""
                }).ToList()
            };

            return Results.Json(response, W3cJsonContext.Default.W3cSuggestResponse);
        });

        // GET /suggest/property?prefix=...
        endpoints.MapGet($"{prefix}/suggest/property", async (HttpContext ctx, WikidataReconciler reconciler) =>
        {
            var prefixParam = ctx.Request.Query["prefix"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(prefixParam))
                return Results.Json(new W3cSuggestResponse(), W3cJsonContext.Default.W3cSuggestResponse);

            var language = GetAcceptLanguage(ctx);
            if (prefixParam.Length > serviceOptions.MaxQueryLength)
                return Results.Problem(statusCode: 400, detail: "The prefix exceeds MaxQueryLength.");
            var results = await reconciler.SuggestPropertiesAsync(prefixParam, 7, language, ctx.RequestAborted);

            var response = new W3cSuggestResponse
            {
                Result = results.Select(r => new W3cSuggestItem
                {
                    Id = r.Id,
                    Name = r.Name,
                    Description = r.Description ?? ""
                }).ToList()
            };

            return Results.Json(response, W3cJsonContext.Default.W3cSuggestResponse);
        });

        // GET /suggest/type?prefix=...
        endpoints.MapGet($"{prefix}/suggest/type", async (HttpContext ctx, WikidataReconciler reconciler) =>
        {
            var prefixParam = ctx.Request.Query["prefix"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(prefixParam))
                return Results.Json(new W3cSuggestResponse(), W3cJsonContext.Default.W3cSuggestResponse);

            var language = GetAcceptLanguage(ctx);
            if (prefixParam.Length > serviceOptions.MaxQueryLength)
                return Results.Problem(statusCode: 400, detail: "The prefix exceeds MaxQueryLength.");
            var results = await reconciler.SuggestTypesAsync(prefixParam, 7, language, ctx.RequestAborted);

            var response = new W3cSuggestResponse
            {
                Result = results.Select(r => new W3cSuggestItem
                {
                    Id = r.Id,
                    Name = r.Name,
                    Description = r.Description ?? ""
                }).ToList()
            };

            return Results.Json(response, W3cJsonContext.Default.W3cSuggestResponse);
        });

        // ─── Preview Endpoint ─────────────────────────────────────────

        // GET /preview?id=Q42
        endpoints.MapGet($"{prefix}/preview", async (HttpContext ctx, WikidataReconciler reconciler) =>
        {
            var id = ctx.Request.Query["id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(id))
                return Results.BadRequest("Missing 'id' parameter");

            var language = GetAcceptLanguage(ctx);
            var lang = language ?? "en";

            // Fetch entity data, image, and Wikipedia URL concurrently
            var entitiesTask = reconciler.GetEntitiesAsync([id], lang, ctx.RequestAborted);
            var imagesTask = reconciler.GetImageUrlsAsync([id], lang, ctx.RequestAborted);
            var wikiUrlsTask = reconciler.GetWikipediaUrlsAsync([id], lang, ctx.RequestAborted);

            await Task.WhenAll(entitiesTask, imagesTask, wikiUrlsTask);

            var entities = await entitiesTask;
            var images = await imagesTask;
            var wikiUrls = await wikiUrlsTask;

            if (!entities.TryGetValue(id, out var entity))
                return Results.NotFound();

            var label = HttpUtility.HtmlEncode(entity.Label ?? id);
            var description = HttpUtility.HtmlEncode(entity.Description ?? "");
            images.TryGetValue(id, out var imageUrl);
            wikiUrls.TryGetValue(id, out var wikiUrl);
            var entityUrl = $"https://www.wikidata.org/wiki/{id}";

            var html = BuildPreviewHtml(id, label, description, imageUrl, wikiUrl ?? entityUrl);

            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(html, ctx.RequestAborted);
            return Results.Empty;
        });

        return endpoints;
    }

    // ─── Helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the primary language from the Accept-Language header.
    /// Returns null if not present, letting the library use its configured default.
    /// </summary>
    private static string? GetAcceptLanguage(HttpContext ctx)
    {
        var header = ctx.Request.Headers.AcceptLanguage.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header))
            return null;

        // Parse first language tag, ignoring quality values
        // "fr-FR,fr;q=0.9,en;q=0.8" → "fr-FR"
        var firstTag = header.Split(',')[0].Split(';')[0].Trim();
        return string.IsNullOrWhiteSpace(firstTag) || firstTag == "*" ? null : firstTag;
    }

    private static ServiceManifest BuildManifest(ReconciliationServiceOptions options, HttpRequest request, string prefix)
    {
        var baseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}{prefix}";

        return new ServiceManifest
        {
            Versions = ["0.1", "0.2"],
            Name = options.ServiceName,
            IdentifierSpace = options.IdentifierSpace,
            SchemaSpace = options.SchemaSpace,
            View = new ServiceView { Url = options.EntityViewUrl },
            DefaultTypes = options.DefaultTypes.Select(t => new W3cType { Id = t.Id, Name = t.Name }).ToList(),
            Suggest = new SuggestServices
            {
                Entity = new SuggestService { ServiceUrl = $"{baseUrl}/suggest/entity", ServicePath = "" },
                Property = new SuggestService { ServiceUrl = $"{baseUrl}/suggest/property", ServicePath = "" },
                Type = new SuggestService { ServiceUrl = $"{baseUrl}/suggest/type", ServicePath = "" }
            },
            Preview = new PreviewService
            {
                Url = baseUrl + "/preview?id={{id}}",
                Width = 400,
                Height = 100
            },
            Extend = new ExtendService
            {
                ProposeProperties = new SuggestService { ServiceUrl = baseUrl, ServicePath = "/propose_properties" },
                PropertySettings =
                [
                    new PropertySetting
                    {
                        Name = "limit",
                        Label = "Limit",
                        Type = "number",
                        Default = 0,
                        HelpText = "Maximum number of values to return per row (0 for no limit)"
                    }
                ]
            }
        };
    }

    private static ReconciliationRequest MapToRequest(W3cQuery query, string? acceptLanguage = null, int defaultLimit = 5)
    {
        var types = !string.IsNullOrEmpty(query.Type) ? new[] { query.Type } : null;

        var request = new ReconciliationRequest
        {
            Query = query.Query ?? "",
            Types = types,
            Limit = query.Limit > 0 ? query.Limit : defaultLimit,
            Language = acceptLanguage
        };

        if (query.Properties is { Count: > 0 })
        {
            var props = query.Properties
                .Where(p => !string.IsNullOrEmpty(p.Pid) && !string.IsNullOrEmpty(p.V))
                .Select(p => new PropertyConstraint(p.Pid!, p.V!))
                .ToList();

            if (props.Count > 0)
                return new ReconciliationRequest
                {
                    Query = request.Query,
                    Types = types,
                    Limit = request.Limit,
                    Language = acceptLanguage,
                    Properties = props
                };
        }

        return request;
    }

    private static W3cCandidate MapToCandidate(ReconciliationResult result) => new()
    {
        Id = result.Id,
        Name = result.Name,
        Description = result.Description,
        Score = result.Score,
        Match = result.Match,
        Type = result.Types?.Select(t => new W3cType { Id = t }).ToList() ?? []
    };

    private static string BuildPreviewHtml(string id, string label, string description, string? imageUrl, string linkUrl)
    {
        var imageHtml = imageUrl is not null
            ? "<img src=\"" + HttpUtility.HtmlAttributeEncode(imageUrl) + "\" style=\"width:80px;height:80px;object-fit:cover;border-radius:4px;margin-right:12px;float:left;\" alt=\"\">"
            : "";

        var encodedLink = HttpUtility.HtmlAttributeEncode(linkUrl);

        return "<html><head><meta charset=\"utf-8\"><style>" +
            "body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;margin:0;padding:8px;font-size:13px;color:#333}" +
            ".card{display:flex;align-items:flex-start}" +
            ".info{overflow:hidden}" +
            "h3{margin:0 0 4px 0;font-size:14px}" +
            "h3 a{color:#0645ad;text-decoration:none}" +
            "h3 a:hover{text-decoration:underline}" +
            ".qid{color:#999;font-weight:normal;font-size:12px}" +
            ".desc{color:#555;margin:0;line-height:1.4}" +
            "</style></head><body>" +
            "<div class=\"card\">" +
            imageHtml +
            "<div class=\"info\">" +
            "<h3><a href=\"" + encodedLink + "\" target=\"_blank\">" + label + "</a> <span class=\"qid\">(" + id + ")</span></h3>" +
            "<p class=\"desc\">" + description + "</p>" +
            "</div></div></body></html>";
    }
}

// ─── W3C Models ─────────────────────────────────────────────────

internal sealed class ServiceManifest
{
    [JsonPropertyName("versions")]
    public List<string> Versions { get; set; } = [];

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("identifierSpace")]
    public string IdentifierSpace { get; set; } = "";

    [JsonPropertyName("schemaSpace")]
    public string SchemaSpace { get; set; } = "";

    [JsonPropertyName("view")]
    public ServiceView? View { get; set; }

    [JsonPropertyName("defaultTypes")]
    public List<W3cType> DefaultTypes { get; set; } = [];

    [JsonPropertyName("suggest")]
    public SuggestServices? Suggest { get; set; }

    [JsonPropertyName("preview")]
    public PreviewService? Preview { get; set; }

    [JsonPropertyName("extend")]
    public ExtendService? Extend { get; set; }
}

internal sealed class ServiceView
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

internal sealed class SuggestServices
{
    [JsonPropertyName("entity")]
    public SuggestService? Entity { get; set; }

    [JsonPropertyName("property")]
    public SuggestService? Property { get; set; }

    [JsonPropertyName("type")]
    public SuggestService? Type { get; set; }
}

internal sealed class SuggestService
{
    [JsonPropertyName("service_url")]
    public string ServiceUrl { get; set; } = "";

    [JsonPropertyName("service_path")]
    public string ServicePath { get; set; } = "";
}

internal sealed class PreviewService
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

internal sealed class W3cQuery
{
    [JsonPropertyName("query")]
    public string? Query { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("properties")]
    public List<W3cPropertyValue>? Properties { get; set; }
}

internal sealed class W3cPropertyValue
{
    [JsonPropertyName("pid")]
    public string? Pid { get; set; }

    [JsonPropertyName("v")]
    public string? V { get; set; }
}

internal sealed class W3cQueryResponse
{
    [JsonPropertyName("result")]
    public List<W3cCandidate> Result { get; set; } = [];
}

internal sealed class W3cCandidate
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("match")]
    public bool Match { get; set; }

    [JsonPropertyName("type")]
    public List<W3cType> Type { get; set; } = [];
}

internal sealed class W3cType
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class W3cSuggestResponse
{
    [JsonPropertyName("result")]
    public List<W3cSuggestItem> Result { get; set; } = [];
}

internal sealed class W3cSuggestItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

[JsonSerializable(typeof(ServiceManifest))]
[JsonSerializable(typeof(W3cQueryResponse))]
[JsonSerializable(typeof(W3cQuery))]
[JsonSerializable(typeof(W3cSuggestResponse))]
[JsonSerializable(typeof(Dictionary<string, W3cQuery>))]
[JsonSerializable(typeof(Dictionary<string, List<W3cCandidate>>))]
[JsonSerializable(typeof(W3cExtendRequest))]
[JsonSerializable(typeof(W3cExtendResponse))]
[JsonSerializable(typeof(W3cProposeResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class W3cJsonContext : JsonSerializerContext;
