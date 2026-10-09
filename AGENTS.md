# AGENTS.md

## Project Overview

**Tuvima.Wikidata** is a .NET library for working with Wikidata and Wikipedia. It matches text (names, titles, places) to Wikidata entities, fetches structured data, retrieves Wikipedia content, and provides lightweight in-memory entity graph traversal. The reconciliation algorithms are based on [openrefine-wikibase](https://github.com/wetneb/openrefine-wikibase) (Python, MIT), independently re-implemented in C#. The graph module was inspired by dotNetRDF usage patterns, reimplemented as dependency-free adjacency list traversals.

Two NuGet packages:
- `Tuvima.Wikidata` — core library, zero external dependencies
- `Tuvima.Wikidata.AspNetCore` — W3C Reconciliation API middleware for ASP.NET Core

## Architecture (v3.10.0)

`WikidataReconciler` is a thin **facade** that owns a shared `ReconcilerContext` (HttpClient, options, search/fetcher/scorer/type-checker collaborators, shared provider-safe HTTP pipeline, response cache hook, diagnostics, and per-host limiters) and exposes focused **sub-services** as properties:

```
WikidataReconciler (facade, owns ReconcilerContext)
├── Reconcile    → ReconciliationService   (reconcile + batch + stream + suggest)
├── Entities     → EntityService           (entities, properties, external ID, labels, images, revisions, changes)
├── Wikipedia    → WikipediaService        (URLs, summaries, sections, subsection extraction)
├── Editions     → EditionService          (P747 editions, P629 work-for-edition)
├── Children     → ChildrenService         (generic TraverseChildrenAsync, ChildEntityManifest builder)
├── Authors      → AuthorsService          (multi-author split + pen-name resolution)
├── Labels       → LabelsService           (single + batch label lookup with fallback chain)
├── Persons      → PersonsService          (role-aware person search/batch search with occupation filtering, year/work hints, group expansion)  [v3.2]
|-- Bridge       -> BridgeResolutionService (bridge IDs, ordinal hints, ranked candidates, canonical rollups, relationships, diagnostics)  [v3.2]
|-- Series       -> SeriesManifestService   (ordered series manifests, provenance, warnings)  [v3.0.1]

Shared internals (Tuvima.Wikidata.Internal):
├── ReconcilerContext           <- shared state for all sub-services
├── WikidataSearchClient        <- dual search: wbsearchentities + full-text
├── WikidataEntityFetcher       <- wbgetentities in provider-safe chunks up to 50, rank-aware
├── ReconciliationScorer        <- weighted label + property scoring
├── TypeChecker                 <- P31 matching + optional P279 subclass walking
│   └── SubclassResolver        <- BFS P279 walker with in-memory cache
├── ResilientHttpClient         <- shared HTTP pipeline: host throttles, retries, Retry-After, cache, coalescing
├── HostRateLimiterRegistry     <- one limiter instance per provider host
├── ProviderRequest             <- canonical request/cache keys and cacheability policy
├── ProviderJson                <- malformed JSON -> typed provider failures
├── EntityMapper                <- internal JSON DTO -> public model mapping
├── FuzzyMatcher                <- token-sort-ratio (Levenshtein-based)
├── PropertyMatcher             <- type-specific matching (items, dates, quantities, coords, URLs)
├── PropertyPath                <- chained paths like "P131/P17"
└── LanguageFallback            <- "de-ch" -> "de" -> "mul" -> "en"

EntityGraph (graph module — Tuvima.Wikidata.Graph namespace)
├── GraphNode                   <- entity node input model (Qid, Label, Type, WorkQids)
├── GraphEdge                   <- relationship edge input model (SubjectQid, Relationship, ObjectQid)
└── EntityGraph                 <- adjacency list graph with BFS traversal methods

Direction enum (Tuvima.Wikidata root namespace, shared by Graph + ChildrenService)
```

All v1 top-level methods on `WikidataReconciler` remain as delegating shims forwarding to the owning sub-service, so v1 call sites keep working. New code should prefer the sub-service properties.

### Reconciliation Pipeline (4 stages)

1. **Dual Search** — `wbsearchentities` (autocomplete) and `action=query&list=search` (full-text) run concurrently, results merged with full-text first. When types are specified, a CirrusSearch `haswbstatement:P31=QID` query also runs for better type recall. Multi-language search runs full-text once per query and fans out only the autocomplete path per language. Diacritic-insensitive mode adds ASCII-normalized search variants.
2. **Entity Fetching** — `wbgetentities` batched (max 50), fetches labels/descriptions/aliases/claims with language fallback. Optionally includes sitelinks for display-friendly label matching.
3. **Scoring** — `score = (label_score * 1.0 + sum(prop_score * 0.4)) / (1.0 + 0.4 * num_properties)`. Type penalty halves score if type requested but entity has no P31. Unique ID shortcut sets score to 100 on exact authority ID match. Diacritic-insensitive scoring strips accents before comparison. Multi-value constraints average the best match score for each constraint value (e.g., 2 of 2 authors match = full score, 1 of 2 = half). Item-valued constraints accept a QID (exact match) or a name such as `"Neil Gaiman"`, which is fuzzy-matched against the referenced item's labels and aliases (v3.10). Chained property constraints like `P131/P17` are resolved end to end. Before scoring, a per-request `EntityLookup` fetches each chained-path layer and every name-matched item once for all candidates, so referenced entities cost one batched `wbgetentities` call per layer rather than one per candidate. Time constraints compare at the stored precision: decades by decade, centuries and wider within one span.
4. **Type Filtering** — Direct P31 match (multi-type OR logic) or P279 subclass walk (configurable depth, per-request override). One `SubclassResolver` per reconciler keeps its P279 parent cache across requests, including requests that opt in via a per-request depth. Sort by score desc, QID number asc as tiebreaker.

Batches (`ReconcileBatchAsync` / `ReconcileBatchStreamAsync`) run each distinct request once (`Internal/ReconciliationRequestGroups.cs`); identical requests share the result. Equality covers every request option, with query cleaners compared by delegate identity.

## Project Structure

```
src/
├── Tuvima.Wikidata/                         # Core library
│   ├── WikidataReconciler.cs                # Facade — owns ReconcilerContext, exposes 10 sub-services
│   ├── Direction.cs                         # Enum: Outgoing, Incoming (shared with Graph module)
│   ├── WikidataReconcilerOptions.cs         # Configuration: scoring, language, host rate limits, retry/cache/diagnostics
│   ├── ProviderRateLimitOptions.cs          # Per-host MaxConcurrentRequests, RequestsPerSecond, MaxBatchSize
│   ├── IWikidataResponseCache.cs            # Pluggable raw-response cache hook + canonical cache key
│   ├── InMemoryWikidataResponseCache.cs     # Default process-local response cache
│   ├── WikidataDiagnostics.cs               # Request/cache/throttle/retry/batch/failure telemetry snapshots
│   ├── WikidataFailureKind.cs               # Typed failure categories
│   ├── WikidataProviderException.cs         # Typed provider exception for exhausted/rejected provider calls
│   ├── WikidataFailure.cs                   # Recent typed failure record for diagnostics
│   ├── WikidataHttpLogEntry.cs              # Request logging callback event shape
│   ├── ReconciliationRequest.cs             # Query, Types, ExcludeTypes, Properties, Language/Languages, Limit, DiacriticInsensitive, Cleaners, TypeHierarchyDepth
│   ├── ReconciliationResult.cs              # Id, Name, Description, Score, Match, Types, Breakdown
│   ├── ScoreBreakdown.cs                    # LabelScore, PropertyScores, TypeMatched, UniqueIdMatch
│   ├── SuggestResult.cs                     # Id, Name, Description
│   ├── PropertyConstraint.cs                # PropertyId, Values (always plural in v2)
│   ├── WikidataEntityInfo.cs                # Id, Label, Description, Aliases, Claims, LastRevisionId, Modified
│   ├── WikidataClaim.cs                     # PropertyId, Rank, Value, Qualifiers, QualifierOrder
│   ├── WikidataValue.cs                     # Kind, RawValue, EntityId, EntityLabel, TimePrecision, Amount, Unit, Lat, Lng
│   ├── EntityRevision.cs                    # EntityId, RevisionId, Timestamp (lightweight staleness check)
│   ├── EntityChange.cs                      # EntityId, ChangeType, Timestamp, User, Comment, RevisionId
│   ├── WikipediaSummary.cs                  # EntityId, Title, Extract, Description, ThumbnailUrl, ArticleUrl, Language
│   ├── WikipediaSection.cs                  # Title, Index, Level, Number, Anchor (TOC entry)
│   ├── ChildEntityInfo.cs                   # EntityId, Label, Description, Ordinal, Properties (TraverseChildrenAsync result shape)
│   ├── ChildEntityRequest.cs                # Manifest-builder input: ParentQid, Kind, MaxPrimary, MaxTotal, Language, CustomTraversal
│   ├── ChildEntityKind.cs                   # Preset enum: TvSeasonsAndEpisodes | MusicTracks | ComicIssues | BookSequels | Custom
│   ├── CustomChildTraversal.cs              # Escape hatch for ChildEntityKind.Custom (RelationshipProperty, Direction, ChildTypeFilter, OrdinalProperty, CreatorRoles)
│   ├── ChildEntityManifest.cs               # Structured output: ParentQid, PrimaryCount, TotalCount, Children
│   ├── ChildEntityRef.cs                    # Single child: Qid, Title, Ordinal, Parent, ReleaseDate, Duration, Creators
│   ├── EditionInfo.cs                       # EntityId, Label, Description, Types, Claims (P747 edition data)
│   ├── AuthorResolutionRequest.cs           # Authors.ResolveAsync input: RawAuthorString, WorkQidHint, Language, DetectPseudonyms
│   ├── AuthorResolutionResult.cs            # Authors.ResolveAsync output: Authors, UnresolvedNames
│   ├── ResolvedAuthor.cs                    # Per-author result: OriginalName, Qid, CanonicalName, RealNameQid (v2.4 Pattern 1), Pseudonyms (v2.3 Pattern 2), RealAuthors (v2.4 Pattern 3), Confidence
│   ├── RealAuthor.cs                        # Lightweight real-author ref used in ResolvedAuthor.RealAuthors: Qid, CanonicalName (v2.4)
│   ├── PersonRole.cs                        # Enum for Persons.SearchAsync: Author|Narrator|Director|Actor|VoiceActor|Composer|Performer|Artist|Screenwriter (v2.1)
│   ├── PersonSearchRequest.cs               # Persons.SearchAsync/SearchBatchAsync input: Name, Role, TitleHint, WorkQid, IncludeMusicalGroups, BirthYearHint, DeathYearHint, CompanionNameHints, ExpandGroupMembers, AcceptThreshold (v3.2)
│   ├── PersonSearchResult.cs                # Persons.SearchAsync output: Found, Qid, CanonicalName, IsGroup, Score, Occupations, NotableWorks, GroupMembers (v2.1)
│   ├── SeriesManifestRequest.cs             # Series manifest input: SeriesQid, language, collection expansion, caps (v3.0.1)
│   ├── SeriesManifestResult.cs              # Ordered manifest output with items, warnings, completeness (v3.0.1)
│   ├── SeriesManifestItem.cs                # Per-work row with ordinal/date/chain evidence, membership scope, and provenance (v3.5)
│   ├── SeriesManifestItemScope.cs           # MainSequence, Supplementary, CollectedContent, BroaderContext, Unpositioned (v3.5)
│   ├── SeriesManifestRelationship.cs        # Relationship evidence for why a manifest item was included (v3.0.1)
│   ├── SeriesManifestWarning.cs             # Non-fatal series data warnings (v3.0.1)
│   ├── SeriesManifestOrderSource.cs         # Ordering source enum (v3.0.1)
│   ├── SeriesManifestCompleteness.cs        # Manifest completeness enum (v3.0.1)
│   ├── BridgeResolutionRequest.cs           # High-level bridge/identity request (v3.0)
│   ├── BridgeResolutionResult.cs            # One result per input with selected/ranked candidates, failure, diagnostics (v3.0)
│   ├── BridgeCandidate.cs                   # Ranked candidate shape with QID, labels, matched property, confidence, reasons (v3.0)
│   ├── CanonicalRollup.cs                   # Resolved entity QID, canonical work QID, relationship path (v3.0)
│   ├── BridgeSeriesInfo.cs                  # Series/order extraction output (v3.0)
│   ├── BridgeRelationshipEdge.cs            # Auditable relationship edges with property IDs (v3.0)
│   ├── SectionContent.cs                    # Title, Content (structured section content for subsection handling)
│   ├── QueryCleaners.cs                     # Built-in title pre-cleaning functions
│   ├── CachingDelegatingHandler.cs          # [Obsolete in v3.10] HTTP caching base class; skips MediaWiki error envelopes. Prefer ResponseCache
│   ├── WikidataTelemetry.cs                 # ActivitySource + Meter names for OpenTelemetry (v3.10)
│   ├── Services/                            # Sub-service facade layer (v2.0.0)
│   │   ├── ReconciliationService.cs         # Reconcile, batch, stream, suggest
│   │   ├── EntityService.cs                 # Entities, properties, external ID, labels, images, revisions, changes
│   │   ├── WikipediaService.cs              # URLs, summaries, sections, subsection extraction
│   │   ├── EditionService.cs                # P747 editions, P629 work-for-edition
│   │   ├── ChildrenService.cs               # TraverseChildrenAsync (generic) + GetChildEntitiesAsync (manifest)
│   │   ├── AuthorsService.cs                # ResolveAsync — multi-author split + pen-name detection
│   │   ├── LabelsService.cs                 # GetAsync, GetBatchAsync with language fallback
│   │   ├── PersonsService.cs                # SearchAsync / SearchBatchAsync — role-aware person search (v3.2)
│   │   ├── SeriesManifestService.cs         # GetManifestAsync ordered series manifests (v3.0.1)
│   │   ├── BridgeResolutionService.cs       # ResolveAsync / ResolveBatchAsync high-level identity bridge (v3.0)
│   ├── Graph/                               # Entity graph traversal module
│   │   ├── EntityGraph.cs                   # Core graph class — adjacency lists, BFS pathfinding, family trees
│   │   ├── GraphNode.cs                     # Entity node input model (Qid, Label, Type, WorkQids)
│   │   └── GraphEdge.cs                     # Relationship edge input model (SubjectQid, Relationship, ObjectQid)
│   ├── Properties/
│   │   └── AssemblyInfo.cs                  # InternalsVisibleTo for tests
│   └── Internal/
│       ├── ReconcilerContext.cs             # Shared state holder for facade and all sub-services
│       ├── WikidataSearchClient.cs          # Dual search + suggest + external ID lookup + type-filtered + multi-language
│       ├── WikidataEntityFetcher.cs         # Entity fetching with rank hierarchy + sitelinks + provider-safe chunking
│       ├── ReconciliationScorer.cs          # Weighted scoring formula + unique ID shortcut + item-label matching + prefetch
│       ├── EntityLookup.cs                  # Per-request memo of referenced entities (chained-path layers, name-matched items)
│       ├── ReconciliationRequestGroups.cs   # Groups identical batch requests so each runs once
│       ├── TypeChecker.cs                   # P31 type matching (sync + async with P279)
│       ├── SubclassResolver.cs              # P279 hierarchy BFS with ConcurrentDictionary cache
│       ├── ResilientHttpClient.cs           # Shared pipeline: retries, Retry-After, maxlag, cache, coalescing, per-host throttles
│       ├── HostRateLimiter.cs               # Per-host concurrency + request pacing primitive
│       ├── HostRateLimiterRegistry.cs       # Host -> limiter registry (Wikidata, Wikipedia, Commons, default)
│       ├── ProviderRequest.cs               # Canonical request/cache key and cacheability policy
│       ├── ProviderJson.cs                  # Source-gen JSON deserialize wrapper with typed malformed-response failures
│       ├── EntityMapper.cs                  # Internal DTO -> public model mapping
│       ├── HtmlTextExtractor.cs             # Lightweight HTML-to-text for Wikipedia parse output
│       ├── FuzzyMatcher.cs                  # Token-sort-ratio string matching + diacritic stripping
│       ├── PropertyMatcher.cs               # Type-specific value matching
│       ├── PropertyPath.cs                  # "P131/P17" chained property resolution
│       ├── LanguageFallback.cs              # Language fallback chain
│       └── Json/
│           ├── WikidataJsonContext.cs        # Source-generated JSON serialization context
│           ├── WbSearchEntitiesResponse.cs   # wbsearchentities API response
│           ├── WbGetEntitiesResponse.cs      # wbgetentities API response (claims, qualifiers, sitelinks)
│           ├── QuerySearchResponse.cs        # Full-text search API response
│           ├── ParseResponse.cs              # action=parse API response (sections, section content)
│           ├── RevisionQueryResponse.cs      # Revision query API response (staleness detection)
│           ├── RecentChangesResponse.cs      # Recent changes API response
│           └── WikipediaSummaryResponse.cs   # Wikipedia summary REST/batch API response
├── Tuvima.Wikidata.AspNetCore/              # ASP.NET Core companion
│   ├── ReconciliationEndpoints.cs           # W3C API endpoints + suggest + preview + data extension wiring + W3C models
│   ├── DataExtension.cs                     # W3C data extension: propose_properties (P1963) + extend rows/meta (v3.10)
│   ├── ReconciliationRequestReader.cs       # Bounded form reading + query/extend payload validation
│   ├── ReconciliationServiceOptions.cs      # Service name, identifier space, default types
│   └── ServiceCollectionExtensions.cs       # AddWikidataReconciliation(): parameterless, options instance, or factory (v3.10)
tests/
└── Tuvima.Wikidata.Tests/
    ├── IntegrationTests.cs                  # Live Wikidata API tests (Category=Integration)
    ├── BehaviorFeaturesTests.cs             # Unit tests for TitleHint, WorkQidHint, ordinal paths, chained property paths, multi-language shaping
    ├── FacadeShapeTests.cs                  # Facade contract tests (v2.0.0: sub-service exposure, DTO shapes)
    ├── AuthorsSplitterTests.cs              # Unit tests for AuthorsService.SplitAuthors
    ├── EntityGraphTests.cs                  # Unit tests for the graph module
    ├── FuzzyMatcherTests.cs                 # Unit tests for fuzzy matching
    ├── PropertyMatcherTests.cs              # Unit tests for property matching
    ├── LanguageFallbackTests.cs             # Unit tests for language fallback
    ├── ResilienceAndStage2Tests.cs          # Unit tests for cancellation and pagination
    ├── BridgeResolutionServiceTests.cs      # Unit tests for bridge batching, ranking, rollups, and Wikipedia summary failures
    ├── SeriesManifestServiceTests.cs        # Unit tests for series discovery, ordering, provenance, warnings
    ├── SeriesManifestIntegrationTests.cs    # Live The Expanse series manifest shape test
    ├── ScoringImprovementsTests.cs          # v3.10: item-label matching, batching, dedupe, date precision, type suggest, cache handler
    ├── DependencyInjectionTests.cs          # v3.10: AddWikidataReconciliation overloads
    ├── TelemetryTests.cs                    # v3.10: ActivitySource spans and Meter instruments
    ├── TestHttpMessageHandler.cs            # Test HTTP shim for deterministic service tests
    └── TestPayloads.cs                      # Shared JSON payload builders for service tests
docs/
├── reconciliation.md                        # Reconciliation usage guide
├── entity-data.md                           # Entity data & Wikipedia content guide
├── series-manifest.md                       # Series manifest retrieval guide
├── graph.md                                 # Graph module guide
├── aspnetcore.md                            # ASP.NET Core integration guide
├── configuration.md                         # Configuration options guide
├── architecture.md                          # Architecture overview
├── migrating-to-v2.md                       # v1 → v2.0.0 migration guide
└── changelog.md                             # Version history
```

## Public API Reference

New code should call sub-services via `reconciler.{Service}.{Method}(...)`. All top-level methods listed here remain on `WikidataReconciler` as delegating shims for v1 source-compat.

### `reconciler.Reconcile` — `ReconciliationService`

| Method | Purpose |
|---|---|
| `ReconcileAsync(query)` | Match text to Wikidata entities |
| `ReconcileAsync(query, type)` | Match with type filter (e.g., "Q5" for humans) |
| `ReconcileAsync(ReconciliationRequest)` | Full options: `Types` (plural only in v2), properties, language/languages, limit, exclude types, diacritics, cleaners |
| `ReconcileBatchAsync(requests)` | Parallel batch with concurrency limiting; identical requests run once |
| `ReconcileBatchStreamAsync(requests)` | `IAsyncEnumerable` — yields results as they complete |
| `SuggestAsync(prefix)` | Entity autocomplete |
| `SuggestPropertiesAsync(prefix)` | Property autocomplete |
| `SuggestTypesAsync(prefix)` | Type/class autocomplete — restricted to classes (P279), falls back to entity suggestions if none |

### `reconciler.Entities` — `EntityService`

| Method | Purpose |
|---|---|
| `GetEntitiesAsync(qids)` | Full entity data with claims and qualifiers |
| `GetEntitiesAsync(qids, resolveEntityLabels)` | Full entity data with auto-resolved entity reference labels |
| `GetPropertiesAsync(qids, propertyIds)` | Specific properties with auto-resolved entity labels |
| `LookupByExternalIdAsync(propertyId, value)` | Find entity by ISBN/IMDB/VIAF/ORCID via haswbstatement |
| `GetPropertyLabelsAsync(propertyIds)` | P569 → "date of birth" |
| `GetImageUrlsAsync(qids)` | Wikimedia Commons image URLs from P18 claims |
| `GetRevisionIdsAsync(qids)` | Lightweight staleness check — returns only revision IDs and timestamps |
| `GetRecentChangesAsync(qids, since)` | Detailed entity change history for audit/monitoring, with continuation handling for long windows |

### `reconciler.Wikipedia` — `WikipediaService`

| Method | Purpose |
|---|---|
| `GetWikipediaUrlsAsync(qids)` | QID → Wikipedia article URL via sitelinks |
| `GetWikipediaSummariesAsync(qids)` | Wikipedia article summaries (extract, thumbnail, URL) |
| `GetWikipediaSummariesAsync(qids, lang, fallbacks)` | Wikipedia summaries with language fallback |
| `GetWikipediaSectionsAsync(qids)` | Wikipedia article table of contents |
| `GetWikipediaSectionContentAsync(qid, index)` | Specific Wikipedia section as plain text |
| `GetWikipediaSectionWithSubsectionsAsync(qid, index)` | Section + subsections as structured list of `SectionContent` |

### `reconciler.Editions` — `EditionService`

| Method | Purpose |
|---|---|
| `GetEditionsAsync(workQid, filterTypes?)` | Fetch editions/translations (P747) of a work entity |
| `GetWorkForEditionAsync(editionQid)` | Find parent work (P629) from an edition |

### `reconciler.Children` — `ChildrenService`

| Method | Purpose |
|---|---|
| `TraverseChildrenAsync(parentQid, property, direction)` | Generic parent → child traversal. `Direction.Outgoing` (default) follows the property forward; `Direction.Incoming` finds entities whose property points to parent. Replaces v1 `GetChildEntitiesAsync(string, string, ...)` and the `^P` reverse-prefix convention. |
| `GetChildEntitiesAsync(ChildEntityRequest)` | **NEW.** Builds a structured `ChildEntityManifest` from presets (`TvSeasonsAndEpisodes`, `MusicTracks`, `ComicIssues`, `BookSequels`, `Custom`). Honours `MaxPrimary`, `MaxTotal`, and `CustomChildTraversal.OrdinalProperty`. |

### `reconciler.Authors` — `AuthorsService`

| Method | Purpose |
|---|---|
| `ResolveAsync(AuthorResolutionRequest)` | **NEW.** Splits multi-author strings (handles `" and "`, `" & "`, `"; "`, `", "`, `" with "`, `"、"`, and "Last, First" form), reconciles each name against Q5, optionally applies `WorkQidHint` as `P800` bibliography context, optionally detects pen names via P742, and captures trailing `et al.` in `UnresolvedNames`. Replaces v1 `GetAuthorPseudonymsAsync` + `PseudonymInfo`. |

### `reconciler.Labels` — `LabelsService`

| Method | Purpose |
|---|---|
| `GetAsync(qid, language, withFallbackLanguage)` | **NEW.** Single-entity label lookup with optional language fallback chain. |
| `GetBatchAsync(qids, language, withFallbackLanguage)` | **NEW.** Batch variant returning `IReadOnlyDictionary<string, string?>` — every valid input QID is present in the result dictionary (`null` means the entity exists but has no label in the requested language; an absent key means the entity was missing or the input was invalid). |

### `reconciler.Persons` — `PersonsService` (v2.1)

| Method | Purpose |
|---|---|
| `SearchAsync(PersonSearchRequest)` | **NEW.** Role-aware person search. Reconciles against Q5 (human) + optionally Q215380/Q5741069 (musical groups). Uses an internal `FrozenDictionary<PersonRole, string[]>` to map roles (`Author`, `Narrator`, `Director`, `Actor`, `VoiceActor`, `Composer`, `Performer`, `Artist`, `Screenwriter`) to canonical P106 occupation QIDs. `IncludeMusicalGroups` is `bool?` with per-role defaults (`Performer` and `Artist` default to true). `BirthYearHint`, `DeathYearHint`, and `WorkQid` feed property constraints; `TitleHint` now feeds the notable-work reranking path when explicit `CompanionNameHints` are absent. When `ExpandGroupMembers` is true and the hit is a group, populates `GroupMembers` from P527. |
| `SearchBatchAsync(IReadOnlyList<PersonSearchRequest>)` | Batch person search that returns one result per input request in the same order and deduplicates identical lookups internally. |

### `reconciler.Bridge` — `BridgeResolutionService` (v3.0)

| Method | Purpose |
|---|---|
| `ResolveAsync(BridgeResolutionRequest)` | Resolves one bridge/identity request. |
| `ResolveBatchAsync(IReadOnlyList<BridgeResolutionRequest>)` | Resolves many bridge/identity requests, grouping identical external-ID lookups by `(propertyId, normalizedValue)` and returning one `BridgeResolutionResult` per correlation key. |

Key design notes:
- The public Stage2 compatibility layer was removed in v3.0. Use one request shape: `BridgeResolutionRequest`.
- Built-in bridge catalog covers Apple, TMDB, IMDb, TVDB, ISBN, OpenLibrary, Google Books, MusicBrainz, ComicVine, Goodreads, ASIN, and custom caller-supplied property keys.
- TV and comic ordinal hints (`SeasonNumber`, `EpisodeNumber`, `IssueNumber`) are scored when Wikidata exposes ordinal claims or qualifiers.
- Results include selected/ranked candidates, typed success/failure, reason codes, warnings, provider diagnostics, canonical P629/P747 rollup path, series/order data, and relationship edges.
- Tuvima.Wikidata does not call retail APIs; it only uses supplied bridge identifiers as Wikidata external-ID values.

### `reconciler.Series` — `SeriesManifestService` (v3.0.1)

| Method | Purpose |
|---|---|
| `GetManifestAsync(seriesQid)` | Builds an ordered manifest from a Wikidata series QID using default options. |
| `GetManifestAsync(SeriesManifestRequest)` | Full options: language, include/expand collections, publication dates, descriptions, max depth, and max items. |

Key design notes:
- Combines incoming P179, incoming P361, incoming P8345, outgoing P527, and optional P527 collection expansion.
- Classifies every manifest row structurally as `MainSequence`, `Supplementary`, `CollectedContent`, `BroaderContext`, or `Unpositioned`; count facts carry the same scope so consumers do not count all relationship paths as one sequence.
- Orders by P1545 series ordinal, then P155/P156 chain, then P577 publication date, then label fallback.
- Preserves raw and parsed decimal ordinals, source properties, relationship provenance, collection parents, warnings, and completeness.
- Exposes every member's own `MediaKind` and direct P31 `InstanceOfQids`; consumers must not infer a row's media kind from the requested container.
- When an item is discovered through direct and expanded paths, only ordering evidence scoped to the requested series can promote it to `MainSequence`.
- The library stays factual and does not decide owned/missing UI behavior.

### `reconciler.Diagnostics` — `WikidataDiagnostics` (v2.6)

| Method | Purpose |
|---|---|
| `GetSnapshot()` | Returns request counts by host/endpoint, cache hits/misses, throttled waits, 429 count, retry count, coalesced request count, batch-size metrics, average latency, and recent typed failures. |

Typed provider failures use `WikidataFailureKind` (`NotFound`, `NoSitelink`, `RateLimited`, `TransientNetworkFailure`, `MalformedResponse`, `Cancelled`). Exhausted HTTP/provider failures throw `WikidataProviderException` with the same kind.

### EntityGraph Methods (Tuvima.Wikidata.Graph)

| Method | Purpose |
|---|---|
| `EntityGraph(nodes, edges)` | Build graph from caller-provided nodes and edges |
| `FindPaths(fromQid, toQid, maxHops)` | BFS pathfinding — all paths between two entities |
| `GetFamilyTree(characterQid, generations, parentRels, childRels)` | Ancestor/descendant traversal with configurable relationship types. Ancestors = outgoing parent edges or incoming child edges; descendants = outgoing child edges or incoming parent edges |
| `FindCrossMediaEntities(minWorks)` | Entities appearing in 2+ distinct works |
| `GetNeighbors(qid)` | All directly connected entities with relationship and direction |
| `GetSubgraph(centerQid, radius)` | Extract ego graph around an entity |
| `NodeCount` | Total nodes in the graph |
| `EdgeCount` | Total directed edges in the graph |

### Configuration Options (WikidataReconcilerOptions)

| Option | Default | Description |
|---|---|---|
| `ApiEndpoint` | Wikidata API | Custom Wikibase endpoint support |
| `Language` | `"en"` | Default search language (overridable per-request) |
| `UserAgent` | `Tuvima.Wikidata/{version} (repo URL)` (`DefaultUserAgent`) | Apps should set their own name + contact per Wikimedia policy |
| `Timeout` | 30s | Per-attempt timeout for headers and body; supplied HttpClient's shorter limit wins |
| `TypePropertyId` | `"P31"` | Instance-of property (custom Wikibase may differ) |
| `PropertyWeight` | 0.4 | Weight per property match (label = 1.0) |
| `AutoMatchThreshold` | 95 | Score threshold for auto-match |
| `AutoMatchScoreGap` | 10 | Min gap over second-best for auto-match |
| `MaxConcurrency` | 5 | Active work items per batch/fan-out, clamped to 1..1024; separate from host HTTP limits |
| `MaxRetries` | 3 | Retry attempts for transient 408/429/5xx failures |
| `RetryBaseDelay` | 1s | Base exponential-backoff delay when Retry-After is absent |
| `MaxRetryDelay` | 30s | Cap for exponential backoff when Retry-After is absent |
| `RetryJitterRatio` | 0.2 | Extra jitter applied to exponential backoff delays |
| `MaxLag` | 5 | Wikimedia maxlag parameter (seconds) |
| `WikidataRateLimit` | 1 concurrent / 1 RPS / 50 batch | Host policy for `www.wikidata.org` |
| `WikipediaRateLimit` | 2 concurrent / 2 RPS / 50 batch | Host policy for each `*.wikipedia.org` host |
| `CommonsRateLimit` | 1 concurrent / 1 RPS / 50 batch | Host policy for `commons.wikimedia.org` |
| `DefaultRateLimit` | 1 concurrent / 1 RPS / 50 batch | Host policy for custom Wikibase or unknown hosts |
| `EnableRequestCoalescing` | `true` | Share identical in-flight GET requests |
| `EnableResponseCaching` | `true` | Cache successful cacheable raw provider responses |
| `ResponseCache` | `InMemoryWikidataResponseCache` | Pluggable raw-response cache implementation |
| `ResponseCacheTtl` | 12h | TTL for successful cacheable responses |
| `RequestLogger` | `null` | Optional callback for request/cache/retry/failure log entries |
| `TypeHierarchyDepth` | 0 | P279 subclass walk depth (0 = off) |
| `IncludeSitelinkLabels` | `false` | Include Wikipedia sitelink titles in scoring label pool |
| `UniqueIdProperties` | 13 IDs | Properties that trigger score=100 shortcut |

### ASP.NET Core Endpoints (MapReconciliation)

| Endpoint | Purpose |
|---|---|
| `GET /reconcile` | W3C service manifest |
| `POST /reconcile` | Reconciliation queries (`query` / `queries`) or data extension (`extend`) |
| `GET /reconcile/propose_properties?type=Q5&limit=10` | Data extension property proposals (from the type's P1963, generic fallback) |
| `GET /reconcile/suggest/entity?prefix=...` | Entity autocomplete |
| `GET /reconcile/suggest/property?prefix=...` | Property autocomplete |
| `GET /reconcile/suggest/type?prefix=...` | Type autocomplete |
| `GET /reconcile/preview?id=Q42` | HTML preview card |

All endpoints respect the `Accept-Language` header. The manifest advertises `versions: ["0.1", "0.2"]` and an `extend` section with a `limit` property setting. Extend requests are validated (QID ids, PID properties, `MaxBatchSize` ids, `MaxPropertiesPerQuery` properties) before provider work; values use best rank (preferred, else normal, never deprecated) and map to `{id,name}`, `{date}`, `{float}`, or `{str}` cells.

DI: `AddWikidataReconciliation()`, `AddWikidataReconciliation(options, configureHttpClient?)`, and `AddWikidataReconciliation(sp => options, configureHttpClient?)` register the facade, all ten sub-services, and the named HttpClient `ServiceCollectionExtensions.HttpClientName`. The old `Action<WikidataReconcilerOptions>` overload is `[Obsolete]` (options are init-only, so it could never change anything).

## Build & Test

```bash
# Build
dotnet build

# Unit tests only
dotnet test --filter "Category!=Integration"

# Integration tests (requires network, hits live Wikidata API)
dotnet test --filter "Category=Integration"

# All tests
dotnet test

# Pack NuGet packages
dotnet pack --configuration Release
```

Offline validation: 271 passing tests per target (.NET 8 and .NET 10), 542 executions. Live integration tests run separately against Wikimedia services.

## Key Design Decisions

- **Zero external dependencies** — only `System.Text.Json` (built into .NET). No FuzzySharp, no Polly, no caching libraries.
- **AOT compatible** — `IsAotCompatible` and `IsTrimmable` set in .csproj. All JSON serialization uses source-generated `JsonSerializerContext` (no reflection).
- **Response cache hook** — the shared HTTP pipeline includes `IWikidataResponseCache` with an in-memory default; consumers can replace it with a durable cache.
- **Dual search** — both `wbsearchentities` and full-text `action=query&list=search` contribute to recall. Multi-language search runs the full-text pass once per query and fans out only the autocomplete path per language.
- **Claim rank hierarchy** — preferred rank values used if available, then normal, deprecated always excluded.
- **Language fallback chain** — exact -> subtag parent -> "mul" -> "en". API requests include all fallback languages.
- **Provider-safe throttling** — every public API on a reconciler shares the same per-host limiter instances; Wikidata defaults to single-flight low-RPS behavior.
- **Retry behavior** — transient 408/429/5xx failures and transport errors are retried with capped exponential backoff and jitter, and `Retry-After` is honored when present.
- **In-flight request coalescing** — identical active requests share one network call and one response.
- **maxlag parameter** — appended to every Wikidata API request per Wikimedia bot etiquette.
- **Chained property paths** — property constraints like `P131/P17` are resolved end to end during scoring by fetching the intermediate entities and evaluating the terminal claim values.
- **Telemetry without dependencies** — `WikidataTelemetry.ActivitySourceName` emits one client span per provider request (host, endpoint, attempts, cache hit, error type, failed-attempt events); `WikidataTelemetry.MeterName` emits `tuvima.wikidata.requests` and `tuvima.wikidata.request.duration`. Both use BCL `System.Diagnostics` types.
- **Graph module: no RDF** — adjacency lists and BFS, not RDF/SPARQL. The operations (pathfinding, family trees, cross-media) don't require a graph database engine.

## Wikidata API Endpoints Used

| API | Purpose |
|---|---|
| `wbsearchentities` | Autocomplete search by label/alias |
| `action=query&list=search` | Full-text search across entity content |
| `wbgetentities` | Fetch entity data (labels, descriptions, aliases, claims, sitelinks) |
| Wikipedia `action=query&prop=extracts|pageimages|info|description` | Batched article summaries with thumbnails |
| Wikipedia `action=parse` | Section TOC (tocdata) and section content (text) |
| `action=query&prop=revisions` | Lightweight revision ID lookup for staleness detection |
| `action=query&list=recentchanges` | Entity change monitoring |
| CirrusSearch `haswbstatement:` | External ID reverse lookup + type-filtered search |

## Phase 1 Reliability (v3.7.0)

- `ProviderJson` checks HTTP 200 MediaWiki `error`/`errors` envelopes and recognized response field types before caching. Temporary maxlag/rate-limit/read-only/database failures retry; malformed responses never cache. Existing invalid cache entries are bypassed and replaced on success.
- `WikidataFailureKind.ProviderRejected` is appended without renumbering existing values. Permanent provider rejections do not retry; missing entities remain `NotFound`.
- `ResilientHttpClient` owns shared operations independently of callers. Each waiter cancels independently; the last waiter leaving or disposal cancels shared work. Operation completion removes tracking and releases limiter resources safely.
- Attempt timeouts cover headers and body after host admission, excluding queue/backoff delays. A supplied client's shorter timeout wins.
- `SubclassResolver` caches direct P279 parents instead of partial ancestor closures. Request depth overrides, including zero, apply to required and excluded types for both text and exact-QID reconciliation.
- Regression files: `tests/Tuvima.Wikidata.Tests/Phase1HttpReliabilityTests.cs` and `tests/Tuvima.Wikidata.Tests/SubclassReliabilityTests.cs` cover these contracts with 27 offline cases.

## Phase 2 Performance (v3.8.0)

- `InMemoryWikidataResponseCache()` retains its parameterless API with defaults of 1,024 entries and 64 MiB of UTF-16 key/response payloads. New constructor `(int maxEntries, long maxSizeBytes = 67108864)` allows positive custom limits. Object overhead is additional. LRU eviction, expiry pruning on cache activity, and replacement accounting occur under one lock; oversized/nonpositive-TTL replacements remove the old entry without storing the new one.
- `Internal/BoundedAsync.cs` provides ordered batch collection and indexed completion streaming with bounded pending tasks. It cancels and observes pending operations on failure, cancellation, and early stream disposal. Applied to reconciliation, author/person batches, Wikipedia fan-out, and multilingual search. `MaxConcurrency` is per operation, with independent nested windows; host limits remain shared and person batches retain their stricter cap.
- `HostRateLimiter` acquires concurrency before spacing admissions using monotonic timestamps. Cancelled waits release capacity without reserving future slots.
- `tests/Tuvima.Wikidata.Tests/Phase2PerformanceTests.cs` contains 13 regression cases for capacity/expiry, concurrent cache updates, pacing backlogs/cancellation, ordered bounded batches, and large streams with early disposal.

## Phase 3 Consumer and Release Confidence (v3.9.0)

- `src/Tuvima.Wikidata.AspNetCore/ReconciliationRequestReader.cs` bounds POST bodies and validates complete form/JSON payloads before provider calls. Invalid payloads return 400 problem details, oversized bodies return 413, and unsupported content types return 415. Cancellation/provider failures are not relabeled as client validation errors.
- `ReconciliationServiceOptions` adds positive `MaxBatchSize` (100), `MaxQueryLength` (500), `MaxResultLimit` (50), `MaxPropertiesPerQuery` (25), and `MaxRequestBodyBytes` (1 MiB). Configure them through `MapReconciliation`; mapping rejects nonpositive settings. Query/type/property strings and nonblank suggest prefixes honor the character limit. Omitted/zero result limit uses min(5, MaxResultLimit).
- Endpoint batches validate every entry before execution, then delegate to bounded reconciliation, preserving correlation keys and cancellation. Single and batch response shapes remain unchanged.
- `tests/Tuvima.Wikidata.Tests/ReconciliationEndpointTests.cs` adds 36 offline HTTP endpoint cases using framework-matched ASP.NET TestHost packages. The test project now targets both net8.0 and net10.0; production packages retain their dependency policies.

## Phase 4 Bridge Structure (v3.9.1)

- `BridgeResolutionService` retains HTTP access, batch orchestration, fallback searches, progress, cancellation, and failure handling. Its implementation is reduced from 1,467 to 668 lines with no public API changes.
- `src/Tuvima.Wikidata/Internal/BridgeCandidateScorer.cs` owns bridge-claim verification, bridge/text candidate scoring, media hints, ordinal normalization, known-ID collection, and deterministic ranking. It receives the configured type-property ID and fetched data, with no HTTP dependency.
- `Internal/BridgeCanonicalRollup.cs` owns P629/P747 work/edition selection and evidence paths. `Internal/BridgeRelationshipExtractor.cs` owns related-QID discovery, series classification, and relationship-edge extraction. Both operate on fetched entity data.
- `Internal/BridgeEntityFacts.cs` contains shared claim readers/property sets; `Internal/BridgeDiagnosticsBuilder.cs` accumulates request diagnostics across phases.
- `tests/Tuvima.Wikidata.Tests/BridgeContractTests.cs` adds seven public behavior checks, verified before and after extraction.
- `benchmarks/Tuvima.Wikidata.Benchmarks/` is a standalone non-packable net8.0/net10.0 console project, outside the solution and CI test gates. It uses local provider fixtures, measures timing/allocations, and emits representative result/public API fingerprints. Commands and baseline evidence are in `docs/performance.md`; benchmark output defaults to stdout or an explicitly supplied file.

## Phase 5 Matching, OpenRefine, and Packaging (v3.10.0)

- Name-valued item constraints, batched chained-path/item prefetch, decade/century date precision, shared P279 resolver, and batch request deduplication (see Reconciliation Pipeline).
- `SuggestTypesAsync` returns classes; W3C data extension (`extend` + `propose_properties`) and manifest `versions`.
- `AddWikidataReconciliation` options-instance and factory overloads; the `Action` overload is obsolete.
- `CachingDelegatingHandler` no longer caches HTTP-200 MediaWiki error envelopes or non-JSON bodies, and is obsolete in favour of `ResponseCache`.
- Default User-Agent carries the real package version; unused `ReconcilerContext.ConcurrencyLimiter` removed.
- `wbgetentities` fails a whole request with `no-such-entity` when any ID does not exist. `WikidataEntityFetcher` drops the ID named in the error and refetches the rest, so nonexistent QIDs are simply absent (Labels return null). `nosuchsection` maps to `NotFound`, so `GetWikipediaSectionContentAsync` returns null for an invalid section.
- Packages ship XML docs (CS1591 suppressed for undocumented DTO members), `.snupkg` symbols, SourceLink metadata, and package validation against the 3.9.1 baseline. CI publishes the exact packed artifact.

## CI/CD

GitHub Actions workflow (`.github/workflows/ci.yml`):
- Build SDK: .NET 10, with .NET 8 and .NET 10 runtimes installed
- Explicit test-target matrix: net8.0 and net10.0; offline core/endpoint tests gate every push/PR, with build warnings treated as errors and per-target TRX artifacts
- Integration tests run once on .NET 10 in a separate `continue-on-error` job (depend on Wikidata availability)
- NuGet pack (`.nupkg` + `.snupkg`, package validation against `PackageValidationBaselineVersion`) as build artifact
- Auto-publish the packed artifact to NuGet on every push to main (requires `NUGET_API_KEY` secret)

## Mandatory Rules

1. **Documentation on every feature change.** Every new public method, property, option, or behavior change MUST be reflected in BOTH `AGENTS.md` / `CLAUDE.md` (kept identical apart from the title; architecture, API reference, project structure) AND `README.md` / relevant `docs/` files. Never ship a feature without updating docs.

2. **Version bump on every feature change.** Any commit that adds, removes, or changes public API surface MUST increment the package version in BOTH `.csproj` files (`Tuvima.Wikidata` and `Tuvima.Wikidata.AspNetCore`). Use semantic versioning:
   - **Patch** (x.y.**Z**) — bug fixes, internal refactors, doc-only changes
   - **Minor** (x.**Y**.0) — new features, new public methods/properties/options, backward-compatible additions
   - **Major** (**X**.0.0) — breaking changes to existing public API

   After publishing a release, move `PackageValidationBaselineVersion` in both `.csproj` files to that version.

3. **Tests must pass.** Run `dotnet build` (0 warnings, 0 errors) and `dotnet test --filter "Category!=Integration"` (all pass) before committing.

## Attribution

Reconciliation algorithms based on [openrefine-wikibase](https://github.com/wetneb/openrefine-wikibase) by Antonin Delpeuch (MIT). Graph module inspired by [dotNetRDF](https://github.com/dotnetrdf/dotnetrdf) usage patterns (MIT). Independent C# implementation — no code copied from either project. See `NOTICE` file.
