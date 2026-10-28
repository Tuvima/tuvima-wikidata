# Tuvima.Wikidata

A .NET library for working with [Wikidata](https://www.wikidata.org/) and [Wikipedia](https://www.wikipedia.org/). It matches text to Wikidata entities, fetches structured data, retrieves Wikipedia content, and provides lightweight in-memory entity graph traversal — all with zero external dependencies.

**In plain English:** You have author names, book titles, or company names. This library figures out which Wikidata item each one refers to, gives you a confidence score, and then lets you enrich your data with everything Wikidata and Wikipedia know about those entities. It can also map relationships between entities — family trees, connection paths, and cross-media appearances — without needing a heavy graph database.

This is the first .NET Wikidata reconciliation library, filling a gap in the ecosystem where only Python and JavaScript implementations previously existed.

## Who Is This For?

- **Data engineers** cleaning and linking datasets to structured identifiers
- **App developers** building search, autocomplete, or knowledge-powered features
- **Library/archive systems** matching catalog records to authority files (VIAF, ISNI, LoC)
- **Research teams** enriching study data with Wikidata's 100M+ items
- **Content platforms** pulling plot summaries, biographies, or descriptions from Wikipedia
- **Media applications** traversing entity relationships — family trees, character connections, cross-media appearances
- **Anyone** who needs to go from messy text to structured, linked data

## What Can It Do?

| You have... | The library gives you... |
|---|---|
| A name like "Douglas Adams" | The Wikidata ID (Q42), confidence score, and auto-match flag |
| A matched entity (Q42) | Date of birth, nationality, works, identifiers, Wikipedia link, profile image |
| A Wikipedia article | Section table of contents, and any section's content as plain text |
| An ISBN or IMDB ID | The matching Wikidata entity, without fuzzy matching |
| A list of 10,000 names | Parallel batch processing with progress streaming |
| A prefix like "Doug..." | Autocomplete suggestions for interactive UIs |
| A name with diacritics like "Shogun" | Matches regardless of accents with diacritic-insensitive mode |
| A work like "Hitchhiker's Guide" | All editions and translations, filterable by type (audiobook, paperback, etc.) |
| A series like "The Expanse" | An ordered manifest of works with provenance, ordering confidence, and warnings |
| A query in Japanese and English | Multi-language search that finds the best match across both languages |
| Cached entity data | Lightweight staleness check — only re-fetch what actually changed |
| A set of related entities | Pathfinding, family trees, and cross-media entity detection via in-memory graph |

## Packages

| Package | Purpose |
|---|---|
| [`Tuvima.Wikidata`](https://www.nuget.org/packages/Tuvima.Wikidata) | Core library — reconciliation, entity data, Wikipedia content, graph traversal |
| [`Tuvima.Wikidata.AspNetCore`](https://www.nuget.org/packages/Tuvima.Wikidata.AspNetCore) | ASP.NET Core middleware for hosting a W3C Reconciliation Service API |

Current release: `3.10.1`

Current offline validation: 271 passing tests on each of .NET 8 and .NET 10 (542 executions). Live integration tests run separately against Wikimedia services.

## Documentation

Guides, reference, migration guides and the changelog live at **[docs.tuvima.com/libraries/wikidata](https://docs.tuvima.com/libraries/wikidata/)**.

## Version Details

| Version | Type | Notes |
|---|---|---|
| `3.10.1` | Patch | Documentation moved to [docs.tuvima.com](https://docs.tuvima.com/libraries/wikidata/); README and package links point there. No code changes. |
| `3.10.0` | Minor | Name-valued item constraints, batched chained-path lookups, OpenRefine data extension, class-only type suggest, DI options overloads, OpenTelemetry tracing/metrics, XML docs and symbols in packages. |
| `3.9.1` | Patch | Extracts bridge scoring, canonical rollups, and relationships into focused internals; adds offline comparison benchmarks. |
| `3.9.0` | Minor | Validated ASP.NET requests, configurable request limits, and explicit .NET 8/.NET 10 test coverage. |
| `3.8.0` | Minor | Bounded response cache, admission-based request pacing, and bounded batch execution. |
| `3.7.0` | Minor | Provider-error validation, independent shared-request cancellation, full-body timeouts, and consistent subclass traversal. |
| `3.6.0` | Minor | Adds per-member media classification and scope resolution based on ordinal/traversal evidence. |
| `3.5.0` | Minor | Adds structural series membership scopes so main sequences, supplemental works, collected content, and broader context can be counted and displayed independently. |
| `3.2.0` | Minor | Adds ordinal-aware bridge scoring for TV/comic ingestion and `PersonsService.SearchBatchAsync(...)`. |
| `3.0.3` | Patch | Exact QID reconciliation now uses direct `wbgetentities` lookup while still honoring type and exclusion constraints. |
| `3.0.2` | Patch | Series manifests now discover incoming P8345 franchise members for franchise-modeled series. |
| `3.0.1` | Patch | Adds `reconciler.Series.GetManifestAsync(...)` for ordered series manifests. |
| `3.0.0` | Breaking | Replaces the public Stage2 API with `reconciler.Bridge` and `BridgeResolutionRequest` / `BridgeResolutionResult`. |

## Installation

```
dotnet add package Tuvima.Wikidata
```

**Targets:** .NET 8.0 (LTS) and .NET 10.0

**Dependencies:** None beyond `System.Text.Json` (built into .NET). AOT compatible and trimmable.

## Quick Start

```csharp
using Tuvima.Wikidata;

using var reconciler = new WikidataReconciler();

// Match text to a Wikidata entity
var results = await reconciler.Reconcile.ReconcileAsync("Douglas Adams");

Console.WriteLine(results[0].Id);          // "Q42"
Console.WriteLine(results[0].Name);        // "Douglas Adams"
Console.WriteLine(results[0].Description); // "English author and humourist (1952-2001)"
Console.WriteLine(results[0].Score);       // 100
Console.WriteLine(results[0].Match);       // true (confident auto-match)
```

```csharp
using Tuvima.Wikidata.Graph;

// Build an entity graph from your data
var graph = new EntityGraph(nodes, edges);

// Find how two characters are connected
var paths = graph.FindPaths("Q937618", "Q312545");

// Build a family tree
var tree = graph.GetFamilyTree("Q937618", generations: 3);

// Find characters appearing in multiple works
var crossMedia = graph.FindCrossMediaEntities(minWorks: 2);
```

## Features

### Reconciliation
Match text to Wikidata entities with dual-search, fuzzy matching, type filtering, property constraints, and confidence scoring.

[Reconciliation guide](https://docs.tuvima.com/libraries/wikidata/reconciliation/) — type filtering, property constraints, batch processing, streaming, scoring breakdown

### Entity Data & Wikipedia Content
Fetch structured entity data, Wikipedia summaries and sections, images, revision history, editions, and child entities.

[Entity data guide](https://docs.tuvima.com/libraries/wikidata/entity-data/) — entity fetching, Wikipedia content, staleness detection, edition discovery, child entities

### Series Manifests
Build ordered literary/media series manifests from Wikidata series and franchise entities using P179, P361, P8345, P527, P1545, P155, P156, and P577.

[Series manifest guide](https://docs.tuvima.com/libraries/wikidata/series-manifest/) — manifest retrieval, ordering confidence, provenance, warnings, and The Expanse example

### Graph Traversal
Lightweight in-memory entity graph for pathfinding, family trees, cross-media detection, and subgraph extraction. Pure C# with adjacency lists — no RDF or SPARQL dependencies.

[Graph module guide](https://docs.tuvima.com/libraries/wikidata/graph/) — building graphs, pathfinding, family trees, cross-media entities, subgraphs

### ASP.NET Core Integration
Host a W3C Reconciliation Service API compatible with OpenRefine and Google Sheets.

[ASP.NET Core guide](https://docs.tuvima.com/libraries/wikidata/aspnetcore/) — DI registration, endpoint mapping, service manifest

### Configuration
Tune scoring, language, type hierarchy, provider-safe host limits, response caching, diagnostics, and HTTP behavior.

[Configuration guide](https://docs.tuvima.com/libraries/wikidata/configuration/) — all options, caching, custom HttpClient, custom Wikibase instances

## Architecture

`WikidataReconciler` is a facade over focused sub-services: `Reconcile`, `Entities`, `Wikipedia`, `Editions`, `Children`, `Authors`, `Labels`, `Persons`, `Bridge`, and `Series`. All sub-services share the same `HttpClient`, options, provider-safe HTTP pipeline, response cache hook, diagnostics, and per-host rate limiters.

The reconciliation pipeline has four stages: dual search, entity fetching, weighted scoring, and type filtering.

[Architecture overview](https://docs.tuvima.com/libraries/wikidata/architecture/) — pipeline stages, internal components, design decisions

## What's New in v3.10.0

- **Item properties accept names.** `new PropertyConstraint("P50", ["Neil Gaiman", "Terry Pratchett"])` now fuzzy-matches against the referenced people's labels and aliases. Previously only QIDs matched and names always scored 0. QIDs still match exactly.
- **Fewer API calls for property constraints.** Chained paths (`P19/P17`) and name-matched items are fetched once per request for all candidates instead of once per candidate.
- **Date precision respected.** A decade-precision date (`1980s`) no longer matches every year; centuries and millennia compare within one span.
- **Batch deduplication.** Identical requests in `ReconcileBatchAsync` / `ReconcileBatchStreamAsync` run once and share the result.
- **Shared subclass cache.** Requests that set `TypeHierarchyDepth` reuse one P279 cache instead of starting cold each time.
- **`SuggestTypesAsync` returns classes** (entities with P279), so "novel" suggests the class rather than individual novels.
- **OpenRefine data extension.** The ASP.NET Core endpoint supports `extend` and `propose_properties` ("Add columns from reconciled values"), and the manifest advertises `versions`.
- **DI that can be configured.** `AddWikidataReconciliation(new WikidataReconcilerOptions { ... })` and a factory overload. The old `Action<WikidataReconcilerOptions>` overload is obsolete because options are init-only.
- **OpenTelemetry.** `WikidataTelemetry.ActivitySourceName` (spans per provider request) and `WikidataTelemetry.MeterName` (request count and duration), with no extra dependencies.
- **`CachingDelegatingHandler`** no longer caches HTTP-200 MediaWiki error envelopes (which made retries replay the error) and is obsolete in favour of `ResponseCache`.
- A nonexistent QID no longer fails a whole entity batch (Wikidata rejects the entire request); it is dropped and the rest are fetched. `Labels.GetAsync` returns null and `GetWikipediaSectionContentAsync` returns null for an invalid section, as documented.
- Default User-Agent now carries the real package version. Packages ship XML docs, symbols (`.snupkg`), and are validated against 3.9.1 for accidental breaking changes.

## What's New in v3.9.1

- Bridge resolution delegates scoring, canonical-work selection, and relationship extraction to focused internal components. Public APIs and selection rules are unchanged.
- Seven additional contract tests cover rollup targets, edition selection, ranking ties, ambiguity warnings, and relationship provenance.
- An offline benchmark harness measures reconciliation, batches, bridge resolution, and graph workloads, and compares representative bridge results and public API signatures. See [performance measurements](https://docs.tuvima.com/libraries/wikidata/performance/).

## What's New in v3.9.0

- ASP.NET reconciliation validates the complete request before contacting Wikidata. Malformed JSON, blank queries, null entries, and invalid properties return HTTP 400 problem details.
- Configure batch size, query length, candidate count, property count, and body-size limits through `MapReconciliation`. Oversized bodies return HTTP 413, including bodies without `Content-Length`; unsupported content types return HTTP 415.
- Valid endpoint batches use bounded reconciliation and preserve correlation keys. Request cancellation reaches provider work.
- Offline tests, including 36 endpoint cases, now run on both .NET 8 and .NET 10 in CI. Live tests run once in a separate nonblocking job.

See the [ASP.NET Core guide](https://docs.tuvima.com/libraries/wikidata/aspnetcore/) for defaults and request examples. Existing clients exceeding the new limits can raise them explicitly.

## What's New in v3.8.0

- The default response cache uses LRU eviction with limits of 1,024 entries and 64 MiB of string payloads. Use `new InMemoryWikidataResponseCache(maxEntries, maxSizeBytes)` to customize capacity.
- Host requests are spaced after a concurrency slot becomes available, so queued work cannot burst through previously reserved start times.
- Batches and variable-size fan-out use bounded execution windows controlled by `MaxConcurrency`. Ordered results and indexed streaming results are preserved; early stream disposal cancels pending work.

See [configuration](https://docs.tuvima.com/libraries/wikidata/configuration/) for capacity limits, batch cancellation, and request pacing.

## What's New in v3.7.0

- Temporary provider errors are retried even when returned with HTTP 200; permanent errors have a distinct `ProviderRejected` failure kind.
- Invalid responses cannot poison the response cache, and old invalid entries are bypassed.
- Callers sharing a request can cancel independently, and timeouts cover response-body reads.
- Subclass matching is independent of lookup order and respects each request's depth override, including zero.

See [configuration](https://docs.tuvima.com/libraries/wikidata/configuration/) for timeout, cancellation, retry, and cache behavior.

## What's New in v3.6.0

- **Member-owned classification.** Every manifest row exposes `MediaKind` and the P31 QIDs that support it; consumers no longer need to inherit the parent container's media type.
- **Path-aware scopes.** Expanded children remain Collected Content unless they have ordering evidence scoped directly to the requested series.
- **Safer immediate-series promotion.** Unknown P179 targets require ordinal or previous/next evidence before becoming immediate series.

## What's New in v3.5.0

- **Scoped series membership.** `SeriesManifestItem.MembershipScope` classifies relationship paths as `MainSequence`, `Supplementary`, `CollectedContent`, `BroaderContext`, or `Unpositioned`.
- **Scoped counts.** `ManifestCountFact.Scope` prevents supplemental P361 works and expanded collection children from inflating the primary sequence total.
- **Source ordering remains factual.** Raw and parsed P1545 values, including decimals, remain unchanged; `OrdinalScopeQid` identifies the container that supplied the number, and unnumbered supplemental works are not assigned invented decimal positions.

## What's New in v3.2.0

Additive ingestion-quality release.

- **Ordinal-aware bridge scoring.** `BridgeResolutionService` now scores `SeasonNumber`, `EpisodeNumber`, and `IssueNumber` hints for TV seasons, TV episodes, and comic issues. Matching candidates receive reason codes such as `season.ordinal.match`, `episode.ordinal.match`, and `issue.ordinal.match`; mismatches are exposed as warnings.
- **Person batch search.** `reconciler.Persons.SearchBatchAsync(...)` returns one `PersonSearchResult` per input request while deduplicating identical requests internally.

```csharp
var episode = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
{
    CorrelationKey = "episode-2",
    MediaKind = BridgeMediaKind.TvEpisode,
    BridgeIds = new Dictionary<string, string> { ["tvdb_episode_id"] = "12345" },
    Title = "Pilot",
    SeasonNumber = 1,
    EpisodeNumber = 2
});
```

## What's New in v3.0.3

Patch release improving direct Wikidata identity lookup.

- **Exact QID reconciliation.** Requests such as `Q155653` now fetch the entity directly with `wbgetentities` instead of sending the QID through text search.
- **Constraint-safe direct lookup.** Direct QID reconciliation still honors requested type and exclusion constraints, so identity lookups remain deterministic without bypassing media-kind safety checks.

## What's New in v3.0.2

Patch release improving film-franchise manifest discovery.

- **Incoming P8345 franchise discovery.** Series manifests now discover items that point to the requested series or franchise through P8345.
- **Better franchise manifests.** Film franchises such as Blade Runner can produce ordered manifests when Wikidata models the group as a franchise instead of a P179 series.

## What's New in v3.0.1

Patch release adding generic series manifest retrieval.

- **`reconciler.Series.GetManifestAsync(...)`** builds an ordered Wikidata series manifest from a parent series QID.
- **Multi-pattern discovery.** The service combines incoming P179, incoming P361, incoming P8345, outgoing P527, and optional P527 collection expansion.
- **Explainable ordering.** Items report whether ordering came from P1545 ordinals, P155/P156 chains, P577 publication dates, label fallback, or mixed evidence.
- **Warnings and provenance.** Results expose source properties, collection parents, relationships, and warnings for incomplete or ambiguous Wikidata data.

```csharp
var manifest = await reconciler.Series.GetManifestAsync("Q19610143"); // The Expanse
foreach (var item in manifest.Items)
    Console.WriteLine($"{item.RawSeriesOrdinal}: {item.Label} ({item.OrderSource})");
```

## What's New in v2.6.0

Provider-safety release for high-volume ingestion.

- **One shared Wikimedia HTTP pipeline.** Wikidata, Wikipedia, and Commons-capable calls flow through the same internal sender for per-host throttling, retries, `Retry-After`, request logging, response-cache hooks, and diagnostics.
- **Safe defaults for Wikidata.** `www.wikidata.org` defaults to one in-flight request and low request pacing. Wikipedia and Commons have separate host limiters, and each public API on a `WikidataReconciler` shares the same limiter instances.
- **In-flight request coalescing.** Identical cacheable requests share one network call while the first one is still running, reducing repeated QID/property/summary bursts during ingestion.
- **Built-in response cache abstraction.** `IWikidataResponseCache` and the default `InMemoryWikidataResponseCache` cache successful entity, label/description, sitelink, Wikipedia summary, and Commons-capable responses. Consumers can plug in a durable provider cache later.
- **Batched Wikipedia summaries.** Summary lookups now use batched MediaWiki API calls and preserve QID-to-summary mapping instead of issuing one REST request per article.
- **Typed provider failures and telemetry.** Exhausted retries throw `WikidataProviderException` with `WikidataFailureKind`, while `reconciler.Diagnostics.GetSnapshot()` reports request counts, cache hits/misses, throttling waits, 429s, retries, batch sizes, average latency, and typed data failures such as missing sitelinks.

Validation for this release: 113 unit tests and 80 live integration tests.

## What's New in v2.5.0

Minor release. The main theme is that the documented capabilities now line up with the real runtime behavior.

- **Chained property constraints are now real end to end.** `new PropertyConstraint("P131/P17", "Q145")` now walks the intermediate entity path during scoring instead of only inspecting the root property.
- **Shared HTTP reliability path.** Wikidata and Wikipedia calls now run through one `ResilientHttpClient` that applies the real outbound `MaxConcurrency` limit, retries transient 408/429/5xx failures, and honors `Retry-After` when the API sends it.
- **`LabelsService` semantics are now documented accurately.** `GetBatchAsync` uses `null` for "entity exists but has no label in that language" and omits keys only when the entity is missing or the input is invalid.
- **Cancellation now propagates correctly.** `WikipediaService` and `AuthorsService` no longer swallow `OperationCanceledException` during soft-fail paths.
- **Long recent-change windows now paginate.** `GetRecentChangesAsync` follows continuation tokens instead of silently truncating after the first 500 rows.
- **Public hints that existed on paper now affect results.** `AuthorResolutionRequest.WorkQidHint`, `PersonSearchRequest.TitleHint`, and `CustomChildTraversal.OrdinalProperty` are all wired into runtime behavior.
- **Stage 2 is smarter and lighter.** Music artist strings now resolve through `PersonsService`, text author strings resolve through `AuthorsService`, bridge-ID resolution skips duplicate lookups, and batch work fans out in parallel while still respecting the shared HTTP limiter.
- **Search fan-out is cheaper.** Multi-language reconciliation now runs full-text search once per query and only fans out the autocomplete path per language.
- **Graph family trees have stricter semantics.** Ancestors now mean outgoing parent edges or incoming child edges; descendants mean outgoing child edges or incoming parent edges.

Validation for this release: 103 unit tests and 80 live integration tests.

## What's New in v2.4.0

Additive release. Closes out pseudonym resolution across all three Wikidata modeling patterns:

- **Solo pen name reverse lookup (Pattern 1)** — looking up "Richard Bachman" now resolves to Stephen King (Q39829) via `haswbstatement:P742`. Both `ResolvedAuthor.Qid` and `ResolvedAuthor.RealNameQid` are populated with the real author's QID.
- **Collective pseudonym expansion (Pattern 3)** — looking up "James S.A. Corey" resolves to the collective pseudonym entity (Q6142591) and populates the new `ResolvedAuthor.RealAuthors` list with Daniel Abraham and Ty Franck. The library walks P527 (has part) on any entity whose P31 includes a pseudonym class (Q16017119, Q4647632, etc.), with Q5 type checking on the parts to filter out non-person members.
- **Pen-name enumeration (Pattern 2)** stayed the same — looking up "Stephen King" still returns his P742 values in `ResolvedAuthor.Pseudonyms` (shipped in v2.3).

```csharp
// Pattern 3: collective pseudonym
var result = await reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
{
    RawAuthorString = "James S.A. Corey",
    DetectPseudonyms = true
});

var author = result.Authors[0];
Console.WriteLine($"Pseudonym entity: {author.Qid}");
foreach (var real in author.RealAuthors!)
    Console.WriteLine($"  real author: {real.CanonicalName} ({real.Qid})");
```

New DTO: `RealAuthor` (lightweight `Qid` + `CanonicalName` ref). Unit tests expanded to 92, integration tests to 37.

## What's New in v3.0.0

Breaking release for media identity workflows.

- **`reconciler.Bridge.ResolveBatchAsync(...)` replaces the public Stage2 API.** Submit `BridgeResolutionRequest` items with known provider IDs, media kind, title/creator/year hints, and rollup preference. Results include the selected candidate, ranked alternatives, typed failure state, diagnostics, and canonical rollup path.
- **Stage2 public types were removed.** `WikidataReconciler.Stage2`, `Stage2Service`, `IStage2Request`, `Stage2Request`, `Stage2Result`, and the concrete Stage2 request types are gone.
- **Bridge catalog expanded.** Built-in mappings and normalization cover ISBN, IMDb, TMDB, TVDB, OpenLibrary, Google Books, Apple Books/Music/TV/iTunes, MusicBrainz, ComicVine, Goodreads, ASIN, and custom caller-supplied Wikidata property keys.
- **Edition/release rollups and relationship extraction.** Bridge results can carry `CanonicalRollup`, series/order data from P179/P1545/P155/P156/P361/P527, and auditable relationship edges for series, universe, parent work, part, and has-part relationships.
- **QID-based Wikipedia summary results.** `WikipediaService` now exposes typed summary/description results with preferred-language fallback, clean no-sitelink/not-found outcomes, source provider, URL, and language fields.

```csharp
var requests = new[]
{
    new BridgeResolutionRequest
    {
        CorrelationKey = "movie-42",
        MediaKind = BridgeMediaKind.Movie,
        BridgeIds = new Dictionary<string, string> { ["imdb_id"] = "tt0371724" },
        Title = "The Hitchhiker's Guide to the Galaxy",
        RollupTarget = BridgeRollupTarget.ReturnWorkAndEdition
    }
};

var results = await reconciler.Bridge.ResolveBatchAsync(requests);
var selected = results["movie-42"].SelectedCandidate;
```

Ordinal hints (`SeasonNumber`, `EpisodeNumber`, and `IssueNumber`) contribute to bridge candidate ranking for TV and comics when Wikidata exposes P1545/P4908/P433 ordinal data.

## What's New in v2.3.0

Additive release closing out the library behavior gaps identified during v2.0–v2.2 integration testing.

- **`PersonsService` musical-group scoring fix.** The P106 (occupation) constraint is now skipped when `IncludeMusicalGroups` is effectively true, so Performer/Artist role searches resolve groups like Daft Punk and Radiohead above the default `AcceptThreshold = 0.80` without workarounds.
- **`PersonSearchRequest.CompanionNameHints` re-ranking is live.** The v2.1 structural signal is now wired to scoring — candidates get +10 per companion hint that fuzzy-matches one of their P800 (notable work) labels. One extra batch round-trip when hints are set; no-op otherwise.
- **`LabelsService.GetBatchAsync` pre-filters malformed QIDs.** Previously Wikidata's `wbgetentities` would reject the whole batch if any single input was malformed, silently dropping every label. The service now filters to syntactically-valid QIDs before calling the API.
- **`ResolvedAuthor.Pseudonyms`** — new field exposing P742 (pseudonym) string values on the resolved author. Looking up "Stephen King" with `DetectPseudonyms = true` now returns `["Richard Bachman"]`.
- **`Stage2Service.PickBestEdition` uses epsilon comparison** for score tiebreaking instead of strict float equality.

Integration test coverage expanded to 34 live-Wikidata tests (from 28), unit tests to 88 (from 85).

## What's New in v2.2.0

Additive release — no breaking changes in the v2 line. This public Stage 2 API was removed in v3.0; use the `reconciler.Bridge` example above for current code.

- **`reconciler.Stage2.ResolveBatchAsync(...)`** — unified Stage 2 resolver for bridge IDs, music albums, and type-filtered text reconciliation. Uses a discriminated marker-interface hierarchy (`BridgeStage2Request`, `MusicStage2Request`, `TextStage2Request`) so the strategy is chosen at compile time, not guessed at runtime. Groups identical requests by natural key across a batch. Supports edition ↔ work pivoting via `EditionPivotRule` with fuzzy-match ranking hints.

The plan's original v1.1.0 primitive expansion is now fully landed across v2.0, v2.1, and v2.2.

## What's New in v2.1.0

Additive release — no breaking changes.

- **`reconciler.Persons.SearchAsync(...)`** — role-aware person search with an internal role → occupation mapping table. Nine `PersonRole` values cover author, narrator, director, actor, voice actor, composer, performer, artist, and screenwriter. `Performer` and `Artist` roles automatically include Q215380 (musical group) + Q5741069 (ensemble) in the type filter. Year and work hints feed property constraints; musical groups can be expanded to their P527 members on demand.

```csharp
var result = await reconciler.Persons.SearchAsync(new PersonSearchRequest
{
    Name = "Daft Punk",
    Role = PersonRole.Performer,   // defaults IncludeMusicalGroups = true
    ExpandGroupMembers = true
});

if (result.Found && result.IsGroup)
{
    Console.WriteLine($"Group {result.CanonicalName} has {result.GroupMembers?.Count ?? 0} members");
}
```

For ingestion-style workloads, use `SearchBatchAsync(...)` to preserve input order while deduplicating identical lookups:

```csharp
var people = await reconciler.Persons.SearchBatchAsync([
    new PersonSearchRequest { Name = "Stephen King", Role = PersonRole.Author },
    new PersonSearchRequest { Name = "Stephen King", Role = PersonRole.Author }
]);
```

**Deferred to v2.2.0:** the Stage 2 resolver with discriminated `IStage2Request` hierarchy, edition pivoting, and bridge/music/text batch grouping.

## What's New in v2.0.0

`WikidataReconciler` is now a thin facade that exposes focused sub-services as properties. Each sub-service owns a slice of the API and can be injected independently in DI. All v1 top-level methods remain as delegating shims, so existing call sites compile unchanged.

```csharp
using var reconciler = new WikidataReconciler();

// Reconciliation + suggest
var matches = await reconciler.Reconcile.ReconcileAsync("Douglas Adams", "Q5");

// Entity + Wikipedia data
var entity = await reconciler.Entities.GetEntitiesAsync(["Q42"]);
var summaries = await reconciler.Wikipedia.GetWikipediaSummariesAsync(["Q42"]);

// NEW: structured label lookup
var label = await reconciler.Labels.GetAsync("Q42", language: "de");

// NEW: multi-author string resolution with pen-name detection
var authors = await reconciler.Authors.ResolveAsync(new AuthorResolutionRequest
{
    RawAuthorString = "Neil Gaiman & Terry Pratchett",
    DetectPseudonyms = true
});

// NEW: structured TV/music/comic/book manifest builder
var seasons = await reconciler.Children.GetChildEntitiesAsync(new ChildEntityRequest
{
    ParentQid = "Q3577037",                // Breaking Bad
    Kind = ChildEntityKind.TvSeasonsAndEpisodes,
    MaxPrimary = 10,
    MaxTotal = 200
});
```

**Breaking changes** — see [`migrating-to-v2`](https://docs.tuvima.com/libraries/wikidata/migrating-to-v2/) for full migration steps:

- `ReconciliationRequest.Type` (singular) removed — use `Types = ["Q5"]`
- `PropertyConstraint.Value` (singular) removed — use `new PropertyConstraint("P569", "1952-03-11")` or `Values = [...]`
- `GetAuthorPseudonymsAsync` + `PseudonymInfo` deleted — use `Authors.ResolveAsync(...)`
- `GetChildEntitiesAsync(parent, "^P179", ...)` renamed to `Children.TraverseChildrenAsync(parent, "P179", Direction.Incoming, ...)` — the `^P` string prefix is gone, use the `Direction` enum
- `Direction` moved from `Tuvima.Wikidata.Graph` to the root `Tuvima.Wikidata` namespace (enclosing-namespace rule means existing code keeps working)

**Deferred to follow-up releases:**

- v2.1.0 — `Persons.SearchAsync` (role-aware person search, musical group handling, year/companion hints)
- v2.2.0 — `Stage2.ResolveBatchAsync` (unified bridge/music/text resolver with discriminated request types and edition pivoting)

See the [changelog](https://docs.tuvima.com/libraries/wikidata/changelog/) for the full version history.

## Acknowledgements

The reconciliation algorithms in this library (dual-search strategy, scoring formula, fuzzy matching approach, type checking, and property matching) are based on [openrefine-wikibase](https://github.com/wetneb/openrefine-wikibase) by [Antonin Delpeuch](https://github.com/wetneb), licensed under the MIT License.

> Antonin Delpeuch. "A survey of OpenRefine reconciliation services." [arXiv:1906.08092](https://arxiv.org/abs/1906.08092)

The configurable Wikibase endpoint support was informed by the [nfdi4culture fork](https://gitlab.com/nfdi4culture/openrefine-reconciliation-services/openrefine-wikibase).

The graph module's use cases (pathfinding, family trees, cross-media entity detection) were originally implemented using [dotNetRDF](https://github.com/dotnetrdf/dotnetrdf) in a consuming application. This module provides a lightweight, dependency-free reimplementation of those specific operations.

This is an independent C# implementation. No code was copied from the original projects. See the [NOTICE](NOTICE) file for full attribution details.

## License

MIT. See [LICENSE](LICENSE) for the full text.
