# Reconciliation

Match text (names, titles, places) to Wikidata entities with confidence scoring.

> **v2.0.0 note.** Reconciliation methods are owned by `reconciler.Reconcile` (a `ReconciliationService`). The top-level `reconciler.ReconcileAsync(...)` calls shown below still work as v1 delegation shims, but new code should prefer `reconciler.Reconcile.ReconcileAsync(...)` for clearer dependencies.

## Basic Usage

```csharp
using Tuvima.Wikidata;

using var reconciler = new WikidataReconciler();

// Simple lookup
var results = await reconciler.Reconcile.ReconcileAsync("Douglas Adams");

// With type filter — only match humans (Q5)
var results = await reconciler.Reconcile.ReconcileAsync("Douglas Adams", "Q5");

// With type filter — only match literary works (Q7725634)
var results = await reconciler.Reconcile.ReconcileAsync("1984", "Q7725634");
```

## Property Constraints

Supply known property values to improve scoring:

```csharp
var results = await reconciler.Reconcile.ReconcileAsync(new ReconciliationRequest
{
    Query = "Douglas Adams",
    Types = ["Q5"],
    Limit = 5,
    Properties =
    [
        new PropertyConstraint("P27", "Q145"),         // country of citizenship: UK
        new PropertyConstraint("P569", "1952-03-11"),  // date of birth
    ]
});
```

> **v2.0 breaking change.** `ReconciliationRequest.Type` (singular) was removed. Use `Types = ["Q5"]` — the single-element list covers the common "one type" case. The `reconciler.ReconcileAsync("Douglas Adams", "Q5")` two-argument overload still works and wraps the type internally.

Property values can be:

| Data type | Example value | Description |
|---|---|---|
| Item (QID) | `"Q145"` | Exact entity match |
| String | `"Douglas Adams"` | Fuzzy string match (token-sort-ratio) |
| External ID | `"118500902"` | Exact match (e.g., GND identifier) |
| Date | `"1952-03-11"` | Precision-aware (year, month, or full date) |
| Quantity | `"42"` | Log-decay curve for numeric proximity |
| URL | `"https://example.com"` | Scheme-normalized exact match |
| Coordinates | `"51.5074,-0.1278"` | Distance-based (score decreases to 0 at 1 km) |

### Multi-Value Property Constraints

When an entity has multiple values for a property (e.g., a book with multiple authors), provide all expected values for proportional scoring:

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Good Omens",
    Properties =
    [
        new PropertyConstraint
        {
            PropertyId = "P50",  // author
            Values = ["Neil Gaiman", "Terry Pratchett"]
        }
    ]
});
// Candidates matching both authors score higher than those matching only one
```

Item-valued properties (author, director, performer, country, ...) accept either a QID (`"Q210059"`, exact match) or a name (`"Neil Gaiman"`). Since v3.10, names are fuzzy-matched against the referenced item's labels and aliases; earlier versions only matched QIDs, so names scored 0. The referenced items are fetched once per request for all candidates.

Dates compare at the precision stored on Wikidata: a full date needs the same day, a year-precision date the same year, a decade-precision date (`1980s`) the same decade, and century or coarser dates a year within one span.

### Property Paths

Chain properties to match against related entities:

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Douglas Adams",
    Properties =
    [
        new PropertyConstraint("P19", "Q350"),     // place of birth: Cambridge (direct)
        new PropertyConstraint("P19/P17", "Q145"), // place of birth -> country: UK (chained)
    ]
});
```

As of v2.5.0, chained paths are scored end to end. The reconciler fetches the intermediate entities and evaluates the terminal property values instead of only inspecting the root property. Since v3.10, each layer is fetched once for all candidates in a single batched request.

## Exclude Types

Remove candidates of specific types:

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Cambridge",
    ExcludeTypes = ["Q17442446"],  // exclude Wikimedia internal items
});
```

## Batch Reconciliation

Reconcile multiple queries with automatic provider-safe limiting. As of v2.6.0, all outbound provider requests pass through shared per-host limiters (`WikidataRateLimit`, `WikipediaRateLimit`, `CommonsRateLimit`) rather than relying only on top-level batch concurrency:

```csharp
var results = await reconciler.Reconcile.ReconcileBatchAsync([
    new ReconciliationRequest { Query = "Douglas Adams", Types = ["Q5"] },
    new ReconciliationRequest { Query = "Albert Einstein", Types = ["Q5"] },
    new ReconciliationRequest { Query = "Nineteen Eighty-Four", Types = ["Q7725634"] },
]);
```

### Streaming Batch

For large datasets, use `ReconcileBatchStreamAsync` to process results as they arrive:

```csharp
var requests = LoadThousandsOfRequests();
var completed = 0;

await foreach (var (index, results) in reconciler.ReconcileBatchStreamAsync(requests))
{
    completed++;
    Console.WriteLine($"[{completed}/{requests.Count}] {requests[index].Query} -> {results[0].Id}");
    SaveResult(index, results);
}
```

## Suggest / Autocomplete

For interactive UIs with type-ahead search:

```csharp
var entities = await reconciler.SuggestAsync("Douglas");
var properties = await reconciler.SuggestPropertiesAsync("date");
var types = await reconciler.SuggestTypesAsync("book"); // classes only (entities with P279) since v3.10
```

## Multi-Type Filtering with CirrusSearch

Filter by multiple types (OR logic) with per-request subclass depth override:

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Shogun",
    Types = ["Q5398426", "Q15416"],  // TV series OR TV program
    TypeHierarchyDepth = 3,          // walk P279 up to 3 levels
});
```

## Multi-Language Search

Search in multiple languages concurrently (results deduplicated by QID):

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "千と千尋の神隠し",
    Languages = ["ja", "en"],
});
```

As of v2.5.0, the full-text search path runs once per query and only the autocomplete path fans out per language.

## Diacritic-Insensitive Search

Match entities regardless of accents:

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Shogun",
    DiacriticInsensitive = true,  // matches "Shogun"
});
```

## Query Pre-Cleaning

Strip noise from queries before search:

```csharp
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "The Hitchhiker's Guide to the Galaxy (Unabridged)",
    Cleaners = [QueryCleaners.StripParenthetical()],
});

// Or use all built-in cleaners at once
var results = await reconciler.ReconcileAsync(new ReconciliationRequest
{
    Query = "Dune: Part Two S01E03 (Special Edition)",
    Cleaners = QueryCleaners.All(),
});
```

## Score Breakdown

Every result includes a detailed `Breakdown` explaining how the score was computed:

```csharp
var results = await reconciler.Reconcile.ReconcileAsync(new ReconciliationRequest
{
    Query = "Douglas Adams",
    Types = ["Q5"],
    Properties = [new PropertyConstraint("P27", "Q145")]
});

var b = results[0].Breakdown!;
Console.WriteLine($"Label match:    {b.LabelScore}");
Console.WriteLine($"P27 match:      {b.PropertyScores["P27"]}");
Console.WriteLine($"Type matched:   {b.TypeMatched}");
Console.WriteLine($"Weighted score: {b.WeightedScore}");
Console.WriteLine($"Type penalty:   {b.TypePenaltyApplied}");
```

## Reverse Lookup by External ID

Find an entity by ISBN, IMDB ID, ORCID, or any external identifier. This is the low-level primitive; for high-level bridge resolution with ranking, diagnostics, text fallback, and edition/work rollups, use `reconciler.Bridge.ResolveBatchAsync(...)` (v3.0+).

```csharp
var results = await reconciler.Entities.LookupByExternalIdAsync("P214", "113230702"); // VIAF
var results = await reconciler.Entities.LookupByExternalIdAsync("P212", "978-0-345-39180-3"); // ISBN
var results = await reconciler.Entities.LookupByExternalIdAsync("P345", "tt0371724"); // IMDB
```

## Bridge Resolution (v3.0)

For workflows that combine provider IDs, title/creator/year hints, Wikipedia identity, and edition-to-work rollups in one batch, use `reconciler.Bridge`:

```csharp
var requests = new[]
{
    new BridgeResolutionRequest
    {
        CorrelationKey = "book-42",
        MediaKind = BridgeMediaKind.Book,
        BridgeIds = new Dictionary<string, string> { ["isbn13"] = "9780441172719" },
        Title = "Dune",
        Creator = "Frank Herbert",
        RollupTarget = BridgeRollupTarget.ReturnWorkAndEdition
    }
};

var results = await reconciler.Bridge.ResolveBatchAsync(requests);
var result = results["book-42"];
Console.WriteLine(result.SelectedCandidate?.Qid);
Console.WriteLine(result.CanonicalWorkQid);
```

Bridge results include ranked candidates, reason codes, warnings, typed failures, rollup path, series/order metadata, relationship edges, and provider diagnostics.

For TV and comics, pass ordinal hints when available. `SeasonNumber`, `EpisodeNumber`, and `IssueNumber` are scored against Wikidata ordinal claims and qualifiers, and matching/mismatching evidence is exposed in candidate reason codes and warnings.

## Direct QID Lookup

If you already have a QID:

```csharp
var results = await reconciler.Reconcile.ReconcileAsync("Q42");
// results[0].Id == "Q42", Score == 100
```

Exact QID requests are fetched directly with `wbgetentities`; they do not go
through text search. If you include `Types` or `ExcludeTypes`, those constraints
are still applied before a result is returned.

If you just need the display label without full reconciliation, use `reconciler.Labels.GetAsync("Q42")` instead — one round-trip, no scoring.

## Person Search with Role Awareness (v2.1)

For named-entity person resolution with role-based occupation filtering and optional musical-group inclusion:

```csharp
var result = await reconciler.Persons.SearchAsync(new PersonSearchRequest
{
    Name = "Stephen King",
    Role = PersonRole.Author,
    TitleHint = "The Shining",
    BirthYearHint = 1947
});

if (result.Found)
    Console.WriteLine($"{result.CanonicalName} ({result.Qid})");
```

If `CompanionNameHints` is empty, `TitleHint` now feeds the same notable-work re-ranking path.

For bulk ingestion, `SearchBatchAsync(...)` preserves input order and deduplicates identical requests:

```csharp
var results = await reconciler.Persons.SearchBatchAsync([
    new PersonSearchRequest { Name = "Stephen King", Role = PersonRole.Author },
    new PersonSearchRequest { Name = "Stephen King", Role = PersonRole.Author }
]);
```

## Result Object

Each `ReconciliationResult` contains:

| Property | Type | Description |
|---|---|---|
| `Id` | `string` | Wikidata entity ID (e.g., `"Q42"`) |
| `Name` | `string` | Entity label in the requested language |
| `Description` | `string?` | Entity description |
| `Score` | `double` | Confidence score from 0 to 100 |
| `Match` | `bool` | `true` if this is a confident automatic match |
| `Types` | `IReadOnlyList<string>?` | P31 type IDs |
| `MatchedLabel` | `string?` | The label/alias that best matched the query |
| `Breakdown` | `ScoreBreakdown?` | Detailed scoring breakdown |

Results are sorted by score descending, with QID number as a tiebreaker (lower QID = older, more established entity).
