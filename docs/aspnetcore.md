# ASP.NET Core Integration

Host a W3C Reconciliation Service API compatible with OpenRefine and Google Sheets.

## Installation

```
dotnet add package Tuvima.Wikidata.AspNetCore
```

## DI Registration

```csharp
// Defaults
services.AddWikidataReconciliation();

// With options (v3.10)
services.AddWikidataReconciliation(new WikidataReconcilerOptions
{
    UserAgent = "MyApp/1.0 (https://example.org; ops@example.org)",
    Language = "de",
    TypeHierarchyDepth = 3
});

// Built from other services, e.g. IConfiguration (v3.10); the factory runs once
services.AddWikidataReconciliation(sp => new WikidataReconcilerOptions
{
    Language = sp.GetRequiredService<IConfiguration>()["Wikidata:Language"] ?? "en"
});

// Add handlers to the named HttpClient (retries/throttling are already built in)
services.AddWikidataReconciliation(options, http => http.AddHttpMessageHandler<MyLoggingHandler>());
```

The older `AddWikidataReconciliation(o => ...)` overload taking `Action<WikidataReconcilerOptions>` is obsolete. Options are init-only, so that callback could never change them.

As of v2.0, `AddWikidataReconciliation` also registers each **sub-service** as a singleton, so consumers can inject a narrow slice of the API instead of depending on the whole facade:

```csharp
public sealed class MyEntityPipeline(
    Tuvima.Wikidata.Services.LabelsService labels,
    Tuvima.Wikidata.Services.AuthorsService authors,
    Tuvima.Wikidata.Services.SeriesManifestService series,
    Tuvima.Wikidata.Services.BridgeResolutionService bridge)
{
    public async Task<string?> ResolveBookAsync(string isbn)
    {
        var result = await bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = isbn,
            MediaKind = BridgeMediaKind.Book,
            BridgeIds = new Dictionary<string, string> { ["isbn13"] = isbn },
            RollupTarget = BridgeRollupTarget.CanonicalWork
        });

        return result.Found ? result.SelectedCandidate?.Label : null;
    }
}
```

All focused sub-services (`ReconciliationService`, `EntityService`, `WikipediaService`, `EditionService`, `ChildrenService`, `AuthorsService`, `LabelsService`, `PersonsService`, `SeriesManifestService`, `BridgeResolutionService`) resolve from the same root `WikidataReconciler`, so they share the same `HttpClient`, options, provider-safe HTTP pipeline, cache hook, diagnostics object, and host limiters.

## Endpoint Mapping

```csharp
app.MapReconciliation("/api/reconcile", options =>
{
    options.ServiceName = "My Wikidata Service";
    options.DefaultTypes =
    [
        new("Q5", "Human"),
        new("Q515", "City"),
        new("Q7725634", "Literary work")
    ];
});
```

## Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /api/reconcile` | Service manifest (name, capabilities, default types) |
| `POST /api/reconcile` | Reconciliation queries (`query` / `queries`) or data extension (`extend`) |
| `GET /api/reconcile/propose_properties?type=Q5&limit=10` | Property proposals for data extension |
| `GET /api/reconcile/suggest/entity?prefix=...` | Entity autocomplete |
| `GET /api/reconcile/suggest/property?prefix=...` | Property autocomplete |
| `GET /api/reconcile/suggest/type?prefix=...` | Type/class autocomplete (classes only) |
| `GET /api/reconcile/preview?id=Q42` | HTML preview card (thumbnail, description, link) |

All endpoints respect the `Accept-Language` header — a French browser automatically gets French labels without extra configuration.

## Data Extension (v3.10)

OpenRefine's "Add columns from reconciled values" works against this service. The manifest advertises `versions: ["0.1", "0.2"]` and an `extend` section.

- `GET /propose_properties?type=Q5&limit=10` proposes the type's "properties for this type" (P1963). Types without P1963 get a generic list (instance of, subclass of, country, inception, image, official website, Freebase ID).
- `POST` with form field `extend={"ids":["Q42"],"properties":[{"id":"P569"},{"id":"P800","settings":{"limit":3}}]}` returns:

```json
{
  "meta": [{ "id": "P569", "name": "date of birth" }, { "id": "P800", "name": "notable work", "settings": { "limit": 3 } }],
  "rows": {
    "Q42": {
      "P569": [{ "date": "1952-03-11T00:00:00+00:00" }],
      "P800": [{ "id": "Q25169", "name": "The Hitchhiker's Guide to the Galaxy" }]
    }
  }
}
```

Values use the best rank (preferred, else normal; deprecated never). Items become `{id, name}`, dates `{date}` (unknown month/day become 01), quantities `{float}`, everything else `{str}`. The `limit` setting caps values per cell (0 = all). Requests are validated before any provider call: ids must be QIDs (at most `MaxBatchSize`), properties must be PIDs (at most `MaxPropertiesPerQuery`).

POST batches use `Reconcile.ReconcileBatchAsync` to bound active queries by `MaxConcurrency`, while shared host policies independently limit HTTP requests. Every entry is validated before any provider work starts. Batch correlation keys and the existing response shapes are preserved, and client cancellation propagates to provider work. Access diagnostics through the injected reconciler's `Diagnostics` property.

## Request Validation and Limits (v3.9.0)

POST accepts `application/x-www-form-urlencoded` or `multipart/form-data` with exactly one `query` or `queries` field containing a JSON object. Raw `application/json` requests are unsupported. Files, repeated query fields, and simultaneous `query` and `queries` values are rejected.

Single-query form value:

```json
{"query":"Douglas Adams","type":"Q5","limit":5}
```

Batch form value:

```json
{"row1":{"query":"Q42"},"row2":{"query":"Q30"}}
```

Set limits independently for each mapped endpoint:

```csharp
app.MapReconciliation("/api/reconcile", options =>
{
    options.MaxBatchSize = 100;
    options.MaxQueryLength = 500;
    options.MaxResultLimit = 50;
    options.MaxPropertiesPerQuery = 25;
    options.MaxRequestBodyBytes = 1024 * 1024;
});
```

| Option | Default | Meaning |
|---|---|---|
| `MaxBatchSize` | 100 | Maximum queries in a batch; empty batches are invalid |
| `MaxQueryLength` | 500 | Maximum UTF-16 characters in query/type/property ID/property value or a nonblank suggest prefix |
| `MaxResultLimit` | 50 | Maximum requested candidates per query; omitted/zero uses the smaller of 5 and this setting |
| `MaxPropertiesPerQuery` | 25 | Maximum property constraints on one query |
| `MaxRequestBodyBytes` | 1,048,576 | Maximum POST body bytes, including form encoding and multipart boundaries |

All limits must be positive; invalid configuration throws during `MapReconciliation`. Clients that previously sent larger requests must split them or raise the appropriate limits. Server/proxy body limits can still reject requests earlier. Bodies without `Content-Length` are read within the same byte limit before form parsing.

Malformed JSON, wrong field types, blank queries, null batch/property entries, blank batch keys, negative/excessive candidate limits, and excessive batch/text/property counts return **400**. Properties require nonblank string `pid` and `v` fields. Oversized bodies return **413**; unsupported content types return **415**. Validation responses use `application/problem+json` with `status`, `title`, and `detail`. Provider failures and cancellation remain distinct from client validation errors. Empty suggest prefixes still return an empty result list.

The endpoint test suite runs in-memory on both supported ASP.NET runtimes and never calls Wikimedia. It checks validation, limits, successful response shapes, localization, multipart forms, and cancellation.

## Manual Registration (No Companion Package)

Register the facade manually with zero extra dependencies:

```csharp
services.AddHttpClient("Wikidata", c =>
    c.DefaultRequestHeaders.UserAgent.ParseAdd("MyApp/1.0 (contact@example.com)"));

services.AddSingleton(sp => new WikidataReconciler(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("Wikidata"),
    new WikidataReconcilerOptions { Language = "en" }));
```

To also inject individual sub-services, add delegating registrations:

```csharp
services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Reconcile);
services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Entities);
services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Labels);
services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Authors);
services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Series);
services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Bridge);
// …plus Wikipedia, Editions, Children, Persons as needed
```

Or just call `AddWikidataReconciliation()` from the companion package — that does all of this for you.
