# Changelog

## v3.10.0

Matching quality, OpenRefine data extension, and packaging release. Backward compatible; validated against the 3.9.1 package baseline.

Reconciliation:
- Item-valued property constraints accept names as well as QIDs. A non-QID value is fuzzy-matched (token-sort ratio, diacritic-aware when requested) against the referenced item's labels and aliases. Fixes the documented `P50 = ["Neil Gaiman", "Terry Pratchett"]` example, which previously scored 0.
- Scoring prefetches referenced entities through a per-request `EntityLookup`: each chained-path layer and every name-matched item is fetched in one batched `wbgetentities` call for all candidates, instead of one call per candidate.
- Time constraints honour coarse precision: decade (8) compares by decade; century (7) and wider match within one span of the stored year (Wikidata stores these inconsistently as first or last year). Previously any year matched.
- `ReconcileBatchAsync` / `ReconcileBatchStreamAsync` run each distinct request once; duplicates share results. Streams yield every input index.
- Requests that opt into P279 walking with `TypeHierarchyDepth` while the global depth is 0 share one resolver, so its parent cache survives across requests.
- `SuggestTypesAsync` restricts suggestions to classes (P279 statements), falling back to entity suggestions when none match.

ASP.NET Core:
- W3C data extension: `POST /reconcile` with `extend={"ids":[...],"properties":[{"id":"P50","settings":{"limit":1}}]}` returns `meta` and `rows`; `GET /reconcile/propose_properties?type=Q5&limit=10` proposes the type's P1963 properties (generic fallback). Values use best rank and map to `{id,name}`, `{date}`, `{float}`, `{str}`. Payloads are validated before provider work.
- Manifest adds `versions: ["0.1", "0.2"]` and an `extend` section.
- `AddWikidataReconciliation()`, `AddWikidataReconciliation(WikidataReconcilerOptions, Action<IHttpClientBuilder>?)`, and `AddWikidataReconciliation(Func<IServiceProvider, WikidataReconcilerOptions>, Action<IHttpClientBuilder>?)`. `Persons` is now registered too. The `Action<WikidataReconcilerOptions>` overload is `[Obsolete]`: options are init-only, so it could never change anything. `ServiceCollectionExtensions.HttpClientName` exposes the named client.

Reliability and diagnostics:
- `CachingDelegatingHandler` skips HTTP-200 MediaWiki `error`/`errors` envelopes and non-JSON bodies. Caching them made every retry replay the cached error. The class is now `[Obsolete]`; use `WikidataReconcilerOptions.ResponseCache`.
- New `WikidataTelemetry`: `ActivitySource` "Tuvima.Wikidata" (one client span per provider request with host, endpoint, attempts, cache hit, status code, error type, and failed-attempt events) and `Meter` "Tuvima.Wikidata" (`tuvima.wikidata.requests`, `tuvima.wikidata.request.duration`).
- `WikidataReconcilerOptions.DefaultUserAgent` carries the real package version (was hard-coded `1.0`). Docs now state that apps should set their own User-Agent with contact details.
- Removed the unused internal `ReconcilerContext.ConcurrencyLimiter`.
- `wbgetentities` rejects a whole request with `no-such-entity` when any ID does not exist. The entity fetcher now drops the ID named in the error and refetches the rest, so one stale or mistyped QID no longer fails a batch of up to 50; missing entities are simply absent. `LabelsService.GetAsync` returns null again for nonexistent QIDs.
- MediaWiki `nosuchsection` maps to `WikidataFailureKind.NotFound`; `GetWikipediaSectionContentAsync` returns null for an invalid section instead of throwing.

Packaging and CI:
- Packages include XML documentation, `.snupkg` symbols, and SourceLink repository metadata; builds are deterministic in CI. Package validation runs against the 3.9.1 baseline.
- CI publishes the exact packages produced by the pack job instead of packing a second time.
- 39 new offline tests (271 per target).

## v3.9.1

Phase 4 structural cleanup and performance baseline.

- Reduce `BridgeResolutionService` from 1,467 to 668 lines by extracting internal `BridgeCandidateScorer`, `BridgeCanonicalRollup`, `BridgeRelationshipExtractor`, `BridgeEntityFacts`, and `BridgeDiagnosticsBuilder` components. HTTP access, batch coordination, progress, cancellation, and failure orchestration remain in the service.
- Preserve public API shape, candidate formulas/order, reason codes, warning behavior, canonical paths, and relationship provenance. No scoring or graph algorithm optimizations are introduced.
- Add seven public contract tests that pass before and after extraction, bringing offline coverage to 232 tests on each supported runtime.
- Add a dependency-free offline benchmark project for reconciliation, batches, bridge resolution, graph traversal, and graph construction, with result/API comparison fingerprints. See `docs/performance.md` for methodology and measured limits.

## v3.9.0

Phase 3 consumer and release confidence.

- Add positive configurable `ReconciliationServiceOptions` limits: `MaxBatchSize` (100), `MaxQueryLength` (500), `MaxResultLimit` (50), `MaxPropertiesPerQuery` (25), and `MaxRequestBodyBytes` (1 MiB). Invalid settings fail at endpoint mapping.
- Validate form encoding, JSON shapes, all batch entries, query text, candidate limits, and properties before provider work. Return HTTP 400 problem details for invalid requests, 413 for oversized bodies, and 415 for unsupported content types. Body limits apply without Content-Length too.
- Preserve single-query response shape and batch correlation keys; use the bounded reconciliation service for endpoint batches and propagate request cancellation.
- Apply query-length limits to nonblank suggest prefixes while preserving empty suggestion responses.
- Add 36 offline endpoint cases using framework-matched ASP.NET TestHost packages. Tests now target net8.0 and net10.0, with 225 offline tests per target.
- CI installs the .NET 10 build SDK and both runtimes, tests each target explicitly, treats build warnings as errors, and retains per-target TRX results. Live tests run once in a separate nonblocking job.

## v3.8.0

Phase 2 performance release.

- Bound the default response cache to 1,024 entries and 64 MiB of UTF-16 key/response payloads; add a configurable constructor while retaining the parameterless constructor.
- Evict least-recently-used responses when full, prune expired entries on cache activity, and skip oversized responses. Replacement and eviction update entry, expiry, recency, and byte accounting atomically.
- Pace host admissions after acquiring concurrency capacity using monotonic time, preventing bursts after a backlog and avoiding reservations for cancelled waits.
- Use bounded execution windows for reconciliation batches/streams, author resolution, person batches, Wikipedia fan-out, and multilingual search. Preserve ordered batch results and indexed streaming results; cancel outstanding work after failure, cancellation, or early stream disposal.
- Keep provider host limits independent from per-batch `MaxConcurrency`; person batches retain their stricter existing cap.
- Add 13 offline regression cases, including a 10,000-item early-exit stream and 5,000 concurrent cache operations.

## v3.7.0

Phase 1 reliability release.

- Added `WikidataFailureKind.ProviderRejected` for permanent provider errors, preserving existing enum values.
- Detect MediaWiki error envelopes even with HTTP 200; retry recognized temporary failures and honor `Retry-After` without caching errors.
- Reject malformed JSON and invalid response field types before caching, and recover from existing invalid cache entries.
- Give coalesced callers independent cancellation; cancel abandoned work and clean up on completion or disposal.
- Apply per-attempt timeouts through response-body reads, respecting a supplied client's shorter timeout and releasing host slots before retrying.
- Cache direct subclass parents so lookup order cannot affect results; honor request depth overrides, including zero, for required and excluded types.
- Added 27 offline regression cases covering provider failures, cache recovery, shared cancellation, disposal, body timeouts, and subclass traversal.

## v3.6.0

Backward-compatible series-manifest classification release.

- Added `SeriesManifestMediaKind`, `SeriesManifestItem.MediaKind`, and `SeriesManifestItem.InstanceOfQids` so every manifest member carries its own auditable media classification.
- Refined duplicate-path scope resolution: expanded collection children without an ordinal scoped to the requested series remain `CollectedContent`.
- Root-scoped ordinals now win when Wikidata supplies conflicting ordinals for a direct member and a nested collection path.
- Unknown P179 containers require ordering evidence before bridge resolution promotes them as immediate series.

## v3.5.0

Backward-compatible series-manifest scope release.

- Added `SeriesManifestItemScope` and `SeriesManifestItem.MembershipScope`, including an `Unpositioned` state for ambiguous direct members beside a positioned main run.
- Added `ManifestCountFact.Scope` and now emits independent counts for main-sequence, supplementary, collected-content, and broader-context rows.
- Incoming P179 and direct P527 rows are main-sequence members; incoming P361 rows are supplementary; expanded P527 children are collected content; explicitly requested P8345 members are broader context.
- Raw and parsed Wikidata ordinals remain unchanged, including decimals, while unnumbered supplemental items remain unnumbered.

## v3.2.0

Additive release improving ingestion-oriented bridge and person matching.

- `BridgeResolutionService` now scores `SeasonNumber`, `EpisodeNumber`, and `IssueNumber` hints for TV seasons, TV episodes, and comic issues.
- Bridge candidates now expose ordinal reason codes such as `season.ordinal.match`, `episode.ordinal.match`, and `issue.ordinal.match`, plus mismatch/missing warnings.
- Added `PersonsService.SearchBatchAsync(...)`, which returns one `PersonSearchResult` per input request and deduplicates identical lookups internally.

## v3.0.3

Patch release improving direct Wikidata identity lookup.

- Exact QID reconciliation requests such as `Q155653` now fetch the entity directly with `wbgetentities` instead of sending the QID through text search.
- Direct QID reconciliation still honors requested type and exclusion constraints, so callers get deterministic identity lookup without bypassing media-kind safety checks.

## v3.0.2

Patch release improving film-franchise manifest discovery.

- Series manifests now discover incoming P8345 franchise members, which lets film franchises such as Blade Runner produce ordered manifests when Wikidata models the group as a franchise instead of a P179 series.

## v3.0.1

Patch release adding generic series manifest retrieval.

- Added `WikidataReconciler.Series` with `SeriesManifestService.GetManifestAsync(...)`.
- Added public manifest DTOs for request options, ordered items, relationship provenance, warnings, ordering source, and completeness.
- Series manifests combine incoming P179, incoming P361, outgoing P527, and optional P527 collection expansion.
- Ordering uses P1545 series ordinals first, then P155/P156 chains, P577 publication dates, and label fallback.
- Added warnings for missing/conflicting ordinals, broken previous/next chains, max-depth/max-item truncation, duplicates, and label-only fallback sorting.
- Added offline tests for series discovery, ordering, provenance, warnings, decimal ordinals, collection expansion, and truncation behavior, plus a live The Expanse shape test.

## v3.0.0

Breaking bridge/identity release.

### Bridge API

- Removed the public Stage2 compatibility layer: `WikidataReconciler.Stage2`, `Stage2Service`, `IStage2Request`, `BridgeStage2Request`, `MusicStage2Request`, `TextStage2Request`, `Stage2Request`, `Stage2Result`, and `Stage2MatchedStrategy`.
- Added `WikidataReconciler.Bridge` with `ResolveAsync(BridgeResolutionRequest)` and `ResolveBatchAsync(IReadOnlyList<BridgeResolutionRequest>)`.
- Added explainable bridge result models: `BridgeResolutionRequest`, `BridgeResolutionResult`, `BridgeCandidate`, `CanonicalRollup`, `BridgeSeriesInfo`, `BridgeRelationshipEdge`, and bridge status/strategy/rollup enums.
- Added an internal bridge catalog and normalizers for Apple Books/Music/TV/iTunes, TMDB, IMDb, TVDB, ISBN, OpenLibrary, Google Books, MusicBrainz, ComicVine, Goodreads, ASIN, and caller-supplied custom Wikidata property keys.
- Bridge resolution groups lookups by `(propertyId, normalizedValue)`, searches each unique external ID once, fetches candidate entities once, ranks locally, and fans results back by correlation key.

### Rollups, relationships, and Wikipedia descriptions

- Added P629 edition/release-to-work rollups and optional P747 work-to-edition discovery through `CanonicalRollup`.
- Added structured series/order extraction from P179, P1545, P155, P156, P361, and P527.
- Added relationship edges for series, universe, parent work, part, and has-part relationships with property IDs for auditability.
- Added QID-based Wikipedia summary and structured description results with preferred-language fallback, clean no-sitelink/not-found outcomes, source provider, article URL, and language fields.

### Migration

- Consumers should replace Stage2 request factories with `BridgeResolutionRequest` and call `reconciler.Bridge.ResolveBatchAsync(...)`.
- Non-Stage2 services remain: Reconcile, Entities, Wikipedia, Editions, Children, Authors, Labels, Persons, and graph APIs.

## v2.6.0

Provider-safety release for high-volume ingestion.

### Shared HTTP pipeline

- **One internal provider path.** Wikidata, Wikipedia, and Commons-capable calls now flow through the same `ResilientHttpClient` pipeline owned by `ReconcilerContext`.
- **Per-host throttling.** `www.wikidata.org`, `*.wikipedia.org`, and `commons.wikimedia.org` each get independent limiters. Wikidata defaults to one in-flight request and low request pacing; limits are configurable through `WikidataReconcilerOptions`.
- **Correct 429 handling.** `Retry-After` is honored when present. Without it, retries use exponential backoff with jitter and stop at `MaxRetries`.
- **In-flight request coalescing.** Identical requests share the same active network call, reducing repeated bursts for the same QIDs, properties, sitelinks, and summaries.
- **Response cache hook.** Added `IWikidataResponseCache`, `WikidataResponseCacheKey`, and default `InMemoryWikidataResponseCache`. Successful cacheable entity, label/description, sitelink, Wikipedia summary, and Commons-capable responses can now be reused or stored by a consumer-provided cache.

### Batching, telemetry, and failures

- **Batched Wikipedia summaries.** `GetWikipediaSummariesAsync` now batches article titles through the MediaWiki API and preserves the result mapping back to each QID.
- **Provider-safe batch sizing.** `ProviderRateLimitOptions.MaxBatchSize` controls chunking for Wikidata entity batches and Wikipedia summary batches, capped at Wikimedia's public API limit.
- **Diagnostics.** Added `WikidataReconciler.Diagnostics` with request counts by host/endpoint, cache hits/misses, throttled waits, 429 count, retry count, coalesced request count, batch-size metrics, average latency, and recent typed failures.
- **Typed provider failures.** Exhausted provider failures now throw `WikidataProviderException` with `WikidataFailureKind` (`NotFound`, `NoSitelink`, `RateLimited`, `TransientNetworkFailure`, `MalformedResponse`, `Cancelled`) instead of leaking raw HTTP exception details.

### Tests and docs

- **Validation expanded to 113 unit tests and 80 live integration tests.** Added tests for `Retry-After`, exponential backoff, coalescing, cache hit/miss behavior, batch chunking, cancellation, 404/429 handling, and malformed JSON.
- **Documentation updated** across `README.md`, `AGENTS.md`, architecture, configuration, ASP.NET Core, and entity/Wikipedia guides for the new pipeline, options, diagnostics, cache hook, and typed failure behavior.

## v2.5.0

Minor release. Aligns the documented contract with the actual runtime behavior, improves reliability under real Wikimedia traffic, and removes a few no-op public hints.

### Reliability and performance

- **Shared resilient request path.** Wikidata and Wikipedia calls now flow through one `ResilientHttpClient` owned by `ReconcilerContext`. The shared sender applies the real outbound `MaxConcurrency` limit, retries transient `408`, `429`, `500`, `502`, `503`, and `504` responses, retries transport failures, and honors `Retry-After` when present.
- **Cancellation now propagates correctly.** `WikipediaService` and `AuthorsService` rethrow `OperationCanceledException` instead of swallowing it inside their soft-fail fallbacks.
- **`GetRecentChangesAsync` now paginates.** Recent-change monitoring follows `rccontinue` tokens until completion instead of silently truncating at the first page.
- **Multi-language search is cheaper.** Reconciliation now runs full-text search once per query and only fans out `wbsearchentities` per language, preserving deterministic merge order while cutting duplicate requests.
- **ASP.NET Core batch reconciliation now parallelizes independent queries.** The endpoint fans out batch items concurrently, while the shared HTTP limiter still caps actual outbound requests.

### Behavior fixes and capability activation

- **Chained property paths are now truly supported.** `PropertyConstraint` paths like `P131/P17` are resolved end to end during scoring by fetching intermediate entities and evaluating the terminal claims instead of inspecting only the root property.
- **`AuthorResolutionRequest.WorkQidHint` now affects matching.** `AuthorsService.ResolveAsync` applies a `P800` notable-work constraint during the initial reconcile pass when the hint is provided.
- **`PersonSearchRequest.TitleHint` now re-ranks candidates.** When explicit `CompanionNameHints` are absent, `TitleHint` now feeds the existing notable-work re-ranking path instead of being ignored.
- **`CustomChildTraversal.OrdinalProperty` is now honored.** Custom child manifests can drive ordering from an explicit ordinal property instead of always defaulting to `P1545`.
- **Stage 2 artist/author strings now resolve to QIDs before scoring.** Music requests resolve artists through `PersonsService`; text requests resolve authors through `AuthorsService`; unresolved free-text names are dropped instead of being applied as ineffective item-property constraints.
- **Stage 2 bridge lookup avoids duplicate fetches.** Bridge-ID resolution now searches by external ID, fetches the entity once, and derives labels and collected bridge IDs from that single snapshot.
- **`EntityGraph.GetFamilyTree` semantics are now strict.** Ancestors mean outgoing parent edges or incoming child edges; descendants mean outgoing child edges or incoming parent edges.

### Tests and docs

- **Stable integration fixtures.** Two brittle live tests that depended on deleted entity `Q190159` now use stable entity-valued-property fixtures on `Q42`.
- **Validation expanded to 103 unit tests and 80 live integration tests.** Added focused tests for cancellation propagation, property-path scoring, title/work hints, custom ordinals, Stage 2 author/artist resolution, recent-changes pagination, and family-tree semantics.
- **Documentation refreshed across `README.md`, `AGENTS.md`, and the usage guides.** The docs now describe the real `LabelsService` semantics (`null` = entity exists but has no label in that language; absent key = missing or invalid entity), request-level concurrency limiting, chained property-path support, Stage 2 behavior, and the stricter graph traversal contract.

## v2.4.1

Patch release — performance fixes for `AuthorsService`. No API changes.

- **Eliminated double-fetch on Pattern 1 reverse P742 lookup.** When `TryReverseP742LookupAsync` resolved a pen name to its real author, it fetched the real author's entity twice — once to read the label and once again inside `GetPseudonymEnrichmentAsync`. The enrichment helper now accepts an optional pre-fetched entity parameter and skips the duplicate `wbgetentities` call. Saves one API round-trip per Richard-Bachman-style reverse resolution.
- **Parallelized multi-author resolution.** `AuthorsService.ResolveAsync` previously processed each name in a multi-author input ("Alice & Bob & Carol & Dave") strictly sequentially — N reconciliation pipelines back-to-back. The loop now fans out via `Task.WhenAll`, gated by the existing `ConcurrencyLimiter` so the parallel name resolutions stay bounded by `WikidataReconcilerOptions.MaxConcurrency` (default 5). For a 4-author input this gives roughly 4× speedup; for single-author inputs (the common case) the cost is one extra `Task.WhenAll` allocation. Result order is preserved.

## v2.4.0

Additive release. Completes the pseudonym-resolution story by handling collective pseudonyms
(James S.A. Corey → Daniel Abraham + Ty Franck) and solo pen names reversed through P742
(Richard Bachman → Stephen King) in addition to the v2.3 pen-name enumeration case.

### New — pseudonym resolution Patterns 1 and 3

`AuthorsService.ResolveAsync` now handles three distinct Wikidata pseudonym modeling patterns:

| Pattern | Example | Field populated |
|---|---|---|
| **1** — solo pen name (no standalone entity) | "Richard Bachman" | `ResolvedAuthor.RealNameQid = Q39829` (Stephen King), `Qid` set to the same value |
| **2** — pen name listed as P742 on real author | "Stephen King" | `ResolvedAuthor.Pseudonyms = ["Richard Bachman"]` (v2.3) |
| **3** — collective pseudonym with dedicated entity | "James S.A. Corey" | `ResolvedAuthor.RealAuthors = [Daniel Abraham, Ty Franck]` |

**Pattern 1 (reverse P742 lookup):** when the initial reconciliation scores below a high-confidence threshold (80), the service runs `haswbstatement:P742="<name>"` against Wikidata's CirrusSearch index. If that reverse lookup finds an entity whose P742 claim contains the input string, the library treats the found entity as the real author and populates both `Qid` and `RealNameQid` with its QID (there is no separate entity for the pen name itself). Confidence is reported as 100 because the P742 claim is an authoritative Wikidata assertion rather than a fuzzy label match. Verified against Wikidata in integration tests.

**Pattern 3 (collective pseudonym expansion):** when the resolved entity's P31 includes one of the pseudonym classes (`Q16017119` collective pseudonym, `Q4647632` pen name, `Q108946349`, `Q2500638`), the service walks P527 (has part) — the property Wikidata actually uses to link collective pseudonyms to their constituent real authors — with P50 (author) and P170 (creator) as defensive fallbacks. Each discovered part is then type-checked for `Q5` (human) to filter out non-person parts that might slip through P527's general-purpose semantics. Verified with James S.A. Corey → Daniel Abraham in live integration tests.

Both patterns fire automatically when `AuthorResolutionRequest.DetectPseudonyms = true` (the default). Setting it to false returns a plain resolution with all pseudonym fields left null.

### New API — `RealAuthor` + `ResolvedAuthor.RealAuthors`

- **`RealAuthor`** (new lightweight DTO) — `Qid` and `CanonicalName`. Used as the element type of the collective-pseudonym expansion list.
- **`ResolvedAuthor.RealAuthors`** (`IReadOnlyList<RealAuthor>?`) — populated when Pattern 3 fires. Null for solo authors, unresolved inputs, and when `DetectPseudonyms = false`.

The existing `ResolvedAuthor.RealNameQid` field is now meaningfully populated (previously always null) when Pattern 1 fires.

### Behavior change — AuthorsService initial reconcile type filter

`AuthorsService.ResolveAsync` previously filtered initial reconciliation to `[Q5]` + the known pseudonym classes. This excluded real matches whose P31 didn't align with the whitelist — for example, James S.A. Corey (Q6142591) has `P31 = [Q10648343, Q16017119, Q127843]`, of which only Q16017119 was in the whitelist, but the label-match search wasn't finding the entity at all when the filter was applied. The type filter has been removed from the initial reconcile; the library now trusts the label match and post-checks P31 during pseudonym enrichment. Risk of false positives ("James" matching unrelated entities) is bounded by the reconciler's top-N limit and the existing accept-threshold logic.

### Test coverage

- **92 unit tests** (up from 88) — added `ResolvedAuthorShapeTests` covering the new DTO shape and Pattern 1 / Pattern 3 field independence.
- **37 new-primitive integration tests** (up from 34) — `AuthorsIntegrationTests` added three live Wikidata tests:
  - Stephen King solo author → `RealAuthors` stays null, `Pseudonyms` populated.
  - James S.A. Corey collective pseudonym → `RealAuthors` contains Daniel Abraham.
  - Richard Bachman reverse P742 lookup → `RealNameQid` resolves to Stephen King's QID.

### Deferred

Nothing material. The pseudonym story is now structurally complete across all three modeling patterns Wikidata uses. Future refinements (e.g., handling pseudonym chains where a pen name's P742 points to another pen name) can be built on top of this foundation if a real case appears.

## v2.3.0

Additive release — no breaking changes. Closes out the library behavior and test gaps
identified during v2.0–v2.2 integration testing.

### Library behavior fixes

- **`PersonsService` no longer penalises musical groups with the P106 occupation constraint.** When `IncludeMusicalGroups` is effectively true (set explicitly, or inherited from the `Performer` / `Artist` role defaults), the service now skips the P106 (occupation) constraint entirely. Musical groups don't carry P106 claims, so the constraint was dragging group candidates below the default `AcceptThreshold` of 0.80 even when they were the correct answer. Previously required consumers to lower the threshold to ~0.5 to get groups; now works at the documented default.
- **`PersonsService` companion-hint re-ranking is now live.** `PersonSearchRequest.CompanionNameHints` was a structural signal in v2.1 but not wired to scoring. v2.3 adds a post-reconciliation re-ranking pass: fetches the top candidates' P800 (notable work) claims, resolves labels in one batch call, and boosts each candidate by 10 points per companion hint that fuzzy-matches (token-sort ratio ≥ 75) one of their notable works. One extra API round-trip when hints are set, no-op otherwise.
- **`LabelsService.GetBatchAsync` pre-filters malformed QIDs.** Wikidata's `wbgetentities` API rejects the entire batch if any single title is malformed — one bad input in a 100-QID batch used to drop every label. The service now filters the input to syntactically-valid QIDs (`Q\d+`) before calling the API. Invalid entries are absent from the result dictionary, same semantics as non-existent entities.
- **`Stage2Service.PickBestEdition` uses epsilon comparison for score tiebreaking.** Strict float equality (`a == b`) was replaced with a tolerance-based check (`Math.Abs(a - b) < 1e-9`) for determining when two edition candidates are effectively tied. In practice this changes behavior only in the rare case where two editions produce identical fuzzy-match scores at different float encodings; QID-ascending tiebreaker then kicks in as documented.

### New API surface

- **`ResolvedAuthor.Pseudonyms`** (`IReadOnlyList<string>?`) — when `DetectPseudonyms = true` and the resolved author has P742 (pseudonym) claims, the raw string values are now exposed on the result. Looking up "Stephen King" populates this with `["Richard Bachman"]`. Replaces the lightweight v2.0 stub which only ever returned null. `RealNameQid` is retained on the DTO but currently always null — reserved for a future reverse-lookup implementation that finds the real author given a pseudonym string input.

### Test coverage

Integration test coverage expanded from 28 to 34 live-Wikidata integration tests:

- `AuthorsIntegrationTests` — new tests for `Pseudonyms` population on Stephen King and verifying that `DetectPseudonyms = false` leaves the list null.
- `ChildrenIntegrationTests` — new tests for the `MusicTracks` preset (OK Computer Q213754) and the `BookSequels` preset (Hitchhiker's Guide Q25169).
- `PersonsIntegrationTests` — new companion-hint re-ranking smoke test. Daft Punk / Radiohead tests reverted to the default `AcceptThreshold = 0.80` (previously had to use 0.5 as a workaround for the P106 penalty that this release fixes).
- `Stage2IntegrationTests` — new edition-pivot smoke test that exercises the `EditionPivotRule` code path with both `WorkClasses` and `EditionClasses` configured.

Unit test coverage expanded from 85 to 88: `LabelsMalformedQidTests` adds three tests verifying the new filter semantics (all-malformed batch, empty string, bare "Q").

### Deferred to future releases

- **Reverse pseudonym lookup** — looking up "Richard Bachman" should populate `RealNameQid` with Stephen King's QID. Requires haswbstatement lookup on P742 string values; deferred because the CirrusSearch indexing story for string-typed claim values needs validation.

## v2.2.2

Patch release — docs-only.

Brings the `docs/` guides in line with the v2.0 facade + sub-services surface. No code changes.

- **`docs/architecture.md`** — replaced the v1 monolithic component diagram with the v2 facade + nine-sub-service layout. Added a design-decisions bullet for the facade pattern and one for Stage 2's discriminated request hierarchy.
- **`docs/entity-data.md`** — every example now calls the sub-service form (`reconciler.Entities.GetEntitiesAsync`, `reconciler.Wikipedia.GetWikipediaSummariesAsync`, etc.). Added sections for `reconciler.Labels`, the `Children.TraverseChildrenAsync` + `GetChildEntitiesAsync` manifest builder, and `Authors.ResolveAsync` (with a callout that the v1 `GetAuthorPseudonymsAsync` + `PseudonymInfo` were removed).
- **`docs/reconciliation.md`** — every example now uses `reconciler.Reconcile.ReconcileAsync(...)`. Every `Type = "Q5"` occurrence was replaced with `Types = ["Q5"]`. Added callouts for the breaking `Type → Types` change, the `reconciler.Stage2` unified resolver, the `reconciler.Labels` shortcut for display-label lookups, and `reconciler.Persons.SearchAsync` for role-aware person resolution.
- **`docs/aspnetcore.md`** — documents that `AddWikidataReconciliation()` now also registers every sub-service as a singleton, so consumers can inject a narrow slice (`LabelsService`, `AuthorsService`, `Stage2Service`, etc.) instead of the whole facade. Added a manual-registration example for the sub-services.

## v2.2.1

Patch release — test-only changes.

- **Integration test coverage for v2.0–v2.2 primitives.** Adds 28 live-Wikidata integration tests across five files: `LabelsIntegrationTests`, `AuthorsIntegrationTests`, `ChildrenIntegrationTests`, `PersonsIntegrationTests`, `Stage2IntegrationTests`. All tests carry `[Trait("Category", "Integration")]` so the default `dotnet test --filter "Category!=Integration"` run remains offline. Tests verify that each primitive actually hits the API, parses results, and returns the documented shape; they deliberately avoid pinning specific QIDs where reconciler scoring can return close-scoring candidates in non-deterministic order.

## v2.2.0

Additive release — no breaking changes on top of v2.1.0.

### New — `reconciler.Stage2` sub-service

A unified Stage 2 resolver that replaces the hand-rolled compose-your-own pattern of
`LookupByExternalIdAsync` + `ReconcileAsync(Types: [...])` + `GetEditionsAsync` every
consumer was re-implementing.

```csharp
// Bridge ID lookup with edition pivoting (e.g. audiobook → work)
var bridge = Stage2Request.Bridge(
    correlationKey: "book-42",
    bridgeIds: new Dictionary<string, string>
    {
        ["isbn13"] = "9780441172719",
        ["openlibrary"] = "OL24229316M"
    },
    wikidataProperties: new Dictionary<string, string>
    {
        ["isbn13"] = "P212",
        ["openlibrary"] = "P648"
    },
    editionPivot: new EditionPivotRule
    {
        WorkClasses = ["Q7725634"],                     // literary work
        EditionClasses = ["Q3331189", "Q122731938"],    // edition, audiobook edition
        PreferEdition = false                           // edition → work pivot only
    });

// Music album
var music = Stage2Request.Music("album-7", "Random Access Memories", "Daft Punk");

// Type-filtered text reconciliation (strict — CirrusSearchTypes required)
var text = Stage2Request.Text("tv-12", "Breaking Bad", ["Q5398426"], author: null);

var results = await reconciler.Stage2.ResolveBatchAsync([bridge, music, text]);
foreach (var (key, result) in results)
{
    Console.WriteLine($"{key}: {result.MatchedBy} → {result.Qid} (IsEdition={result.IsEdition})");
}
```

### Design

- **Discriminated request hierarchy.** `IStage2Request` is a marker interface with three concrete implementations: `BridgeStage2Request`, `MusicStage2Request`, `TextStage2Request`. The service uses pattern matching on the concrete type rather than an auto-detect heuristic — there is no `Stage2Strategy.Auto` enum and no "if Title is non-empty guess Text" logic. Illegal combinations (like "set both BridgeIds and AlbumTitle") are not representable.
- **Strict no-unfiltered-text rule.** `TextStage2Request.CirrusSearchTypes` is `required` and must be non-empty. To explicitly opt into running text reconciliation without a type filter, set `AllowUnfilteredText = true`. Forgetting to provide types throws `ArgumentException` instead of silently resolving nothing.
- **Batch grouping by natural key.** `ResolveBatchAsync` groups identical requests so N callers submitting the same `(isbn13, 9780441172719)` pair share a single API round-trip. Bridge grouping uses the first non-empty ID in preferred order; music uses `(AlbumTitle, Artist)` normalized; text uses `(Title, Author, sorted CirrusSearchTypes)`.
- **Edition pivoting via `EditionPivotRule`.** Media-type-agnostic — callers configure work classes, edition classes, and (optionally) a list of `RankingHint` values for picking the best edition among multiple matches. Ranking uses the library's existing `FuzzyMatcher.TokenSortRatio` against property claim values (or their resolved entity labels for entity-valued properties). Ties are broken by QID number ascending.
- **`Stage2Result` does not leak claims.** The result carries `Qid`, `WorkQid`, `EditionQid`, `IsEdition`, `MatchedBy`, `PrimaryBridgeIdType`, `CollectedBridgeIds`, and `Label`. Consumers who need full entity data should follow up with `reconciler.Entities.GetEntitiesAsync([result.Qid])`.
- **Static factory helpers.** `Stage2Request.Bridge(...)`, `.Music(...)`, `.Text(...)` make dynamic construction ergonomic for consumers that decide the strategy at runtime.

### ASP.NET Core

- `AddWikidataReconciliation()` now also registers `Stage2Service` as a singleton for direct injection.

### Scope notes

- Companion-name hints for `PersonsService.SearchAsync` remain a structural signal without a custom scoring term. Tracked for v2.3.
- The plan's original v1.1.0 scope (as `wikidata-primitives-expansion.md`) is now fully landed across v2.0, v2.1, and v2.2.

## v2.1.0

Additive release — no breaking changes on top of v2.0.0.

### New — `reconciler.Persons` sub-service

A role-aware person search primitive that replaces the hand-rolled `ReconcileAsync(Types: ["Q5"])` + P106 occupation constraints pattern that every consumer was re-implementing.

```csharp
var result = await reconciler.Persons.SearchAsync(new PersonSearchRequest
{
    Name = "Stephen King",
    Role = PersonRole.Author,
    BirthYearHint = 1947,
    WorkQid = "Q208460" // The Shining
});

if (result.Found)
{
    Console.WriteLine($"{result.CanonicalName} ({result.Qid}) — {result.Score:P0}");
}
```

**Features:**

- **Role → occupation mapping** — 9 `PersonRole` values (`Author`, `Narrator`, `Director`, `Actor`, `VoiceActor`, `Composer`, `Performer`, `Artist`, `Screenwriter`) each map to a canonical set of P106 occupation QIDs. The mapping lives in one tested place instead of scattered `switch` statements in consumer code.
- **Musical group defaulting** — `IncludeMusicalGroups` is `bool?` with a role-aware default: `Performer` and `Artist` default to `true` (include Q215380 + Q5741069), all other roles default to `false` (Q5 only). Explicit override still works.
- **Year hints** — `BirthYearHint` and `DeathYearHint` feed P569/P570 soft scoring via `PropertyConstraint`.
- **Work context** — `WorkQid` boosts candidates whose P800 references the work.
- **Group member expansion** — when `ExpandGroupMembers = true` and the resolved entity is a musical group, the result populates `GroupMembers` from P527 (has parts).
- **Accept threshold** — `AcceptThreshold` (default 0.80) controls whether `Found` flips true even when a candidate is returned. Callers can read `Score` to decide their own threshold.

### ASP.NET Core

- `AddWikidataReconciliation()` now also registers `PersonsService` as a singleton for direct injection.

### Deferred

- **Stage 2 resolver** (discriminated `IStage2Request` hierarchy, edition pivoting, bridge/music/text batch grouping) remains planned for **v2.2.0**.
- Companion-name hint scoring is a structural signal today but does not yet drive a custom scoring term. Enhancement tracked for v2.3.

## v2.0.0

**Breaking release** — introduces a facade/sub-service architecture and deletes three deprecated API shapes. See `docs/migrating-to-v2.md` for a step-by-step migration guide.

### New — facade + sub-services

`WikidataReconciler` is now a thin facade that exposes seven focused sub-services as properties. Each sub-service owns a slice of the API surface and can be injected independently:

| Property | Service | Covers |
|---|---|---|
| `reconciler.Reconcile` | `ReconciliationService` | `ReconcileAsync`, `ReconcileBatchAsync`, `ReconcileBatchStreamAsync`, `SuggestAsync`, `SuggestPropertiesAsync`, `SuggestTypesAsync` |
| `reconciler.Entities` | `EntityService` | `GetEntitiesAsync`, `GetPropertiesAsync`, `LookupByExternalIdAsync`, `GetPropertyLabelsAsync`, `GetImageUrlsAsync`, `GetRevisionIdsAsync`, `GetRecentChangesAsync` |
| `reconciler.Wikipedia` | `WikipediaService` | `GetWikipediaUrlsAsync`, `GetWikipediaSummariesAsync`, `GetWikipediaSectionsAsync`, `GetWikipediaSectionContentAsync`, `GetWikipediaSectionWithSubsectionsAsync` |
| `reconciler.Editions` | `EditionService` | `GetEditionsAsync`, `GetWorkForEditionAsync` |
| `reconciler.Children` | `ChildrenService` | `TraverseChildrenAsync` (generic), `GetChildEntitiesAsync` (new manifest builder) |
| `reconciler.Authors` | `AuthorsService` | `ResolveAsync` (new multi-author + pen-name resolver) |
| `reconciler.Labels` | `LabelsService` | `GetAsync`, `GetBatchAsync` (new single/batch label lookup) |

All v1 top-level methods on `WikidataReconciler` remain as delegating shims, so existing v1 call sites compile unchanged. New code should prefer the sub-service properties.

### New — primitives

- **`reconciler.Labels`** — `GetAsync(qid, language, withFallbackLanguage)` and `GetBatchAsync(qids, ...)` replace the manual `GetEntitiesAsync([qid])` + `TryGetValue` dance for pure label lookups. `GetBatchAsync` returns `IReadOnlyDictionary<string, string?>` with every input QID present (no silent-drop).
- **`reconciler.Authors.ResolveAsync(AuthorResolutionRequest)`** — splits multi-author strings on `" and "`, `" & "`, `"; "`, `", "`, `" with "`, and `"、"`, with "Last, First" detection and trailing `et al.` capture. Returns per-author `ResolvedAuthor` entries with optional pen-name information (`RealNameQid`).
- **`reconciler.Children.GetChildEntitiesAsync(ChildEntityRequest)`** — structured manifest builder with presets `TvSeasonsAndEpisodes`, `MusicTracks`, `ComicIssues`, `BookSequels`, and `Custom` escape-hatch. Returns `ChildEntityManifest` with a `Children` list of `ChildEntityRef` (Qid, Title, Ordinal, Parent, ReleaseDate, Duration, Creators). Honours `MaxPrimary` and `MaxTotal` caps deterministically.

### New — ASP.NET Core

- `AddWikidataReconciliation()` now also registers every sub-service (`ReconciliationService`, `EntityService`, `WikipediaService`, `EditionService`, `ChildrenService`, `AuthorsService`, `LabelsService`) so advanced consumers can inject just the slice they need without going through the facade.

### Breaking — removed

- **`ReconciliationRequest.Type` (singular)** — removed. Use `Types` (plural) for every type filter, even when filtering by a single type. v1 had both fields with "`Types` wins" precedence rules, which was a source of confusion.
- **`PropertyConstraint.Value` (singular)** — removed, along with the internal `GetEffectiveValues()` helper. Use `Values` (plural) directly, or the convenience constructor `new PropertyConstraint("P569", "1952-03-11")` which wraps a single value internally.
- **`GetAuthorPseudonymsAsync(string entityQid, ...)`** — removed along with `PseudonymInfo`. Subsumed by `reconciler.Authors.ResolveAsync(...)` which integrates pen-name detection into the multi-author resolution flow.

### Breaking — renamed / moved

- **`GetChildEntitiesAsync(string parentQid, string relationshipProperty, ...)`** — renamed to **`reconciler.Children.TraverseChildrenAsync(parentQid, relationshipProperty, Direction direction = Direction.Outgoing, ...)`**. The `^P` string prefix for reverse traversal is gone; use `Direction.Incoming` instead.
- **`GetChildEntitiesAsync`** (the name) is now taken by the new manifest-builder primitive that accepts a `ChildEntityRequest` and returns a `ChildEntityManifest`.
- **`Direction` enum** — moved from `Tuvima.Wikidata.Graph` to the root `Tuvima.Wikidata` namespace. It is now shared between the graph module and child-entity traversal. For consumers who wrote `using Tuvima.Wikidata.Graph;` and referenced `Direction`, the enum still resolves via C#'s enclosing-namespace rule — no action needed unless you had a fully qualified `Tuvima.Wikidata.Graph.Direction` reference (change to `Tuvima.Wikidata.Direction`).

### Deferred

- **Persons service** (SearchPersonAsync with role → occupation mapping, year hints, group expansion) is planned for **v2.1**.
- **Stage 2 resolver** (ResolveStage2BatchAsync with discriminated Bridge/Music/Text request types and edition pivoting) is planned for **v2.2**.

### Migration

See `docs/migrating-to-v2.md` for before/after snippets and sed-ready find/replace patterns for every break.

## v1.0.0

- **Renamed from `Tuvima.WikidataReconciliation` to `Tuvima.Wikidata`** — package name now reflects the library's full scope: reconciliation, entity data, Wikipedia content, and graph traversal. All public types keep their names; only the namespace changes (`using Tuvima.WikidataReconciliation` becomes `using Tuvima.Wikidata`).
- **Graph module** — new `Tuvima.Wikidata.Graph` namespace with `EntityGraph` for in-memory entity graph traversal. Provides pathfinding (BFS), family tree construction, cross-media entity detection, neighbor lookup, and subgraph extraction. Zero dependencies, AOT compatible, thread-safe. Replaces the need for heavy graph libraries like dotNetRDF for common entity relationship operations.
- **Repository moved** to [github.com/Tuvima/wikidata](https://github.com/Tuvima/wikidata).
- **v1.0.0 stability signal** — the reconciliation API has been production-tested through 10 minor versions.

### Migration from v0.x

1. Update package references:
   - `Tuvima.WikidataReconciliation` -> `Tuvima.Wikidata`
   - `Tuvima.WikidataReconciliation.AspNetCore` -> `Tuvima.Wikidata.AspNetCore`
2. Update namespace imports:
   - `using Tuvima.WikidataReconciliation;` -> `using Tuvima.Wikidata;`
   - `using Tuvima.WikidataReconciliation.AspNetCore;` -> `using Tuvima.Wikidata.AspNetCore;`
3. All public types (`WikidataReconciler`, `ReconciliationRequest`, `ReconciliationResult`, etc.) are unchanged.

## v0.10.0

- **Section heading stripping** — `GetWikipediaSectionContentAsync` now automatically strips the section's own heading from the returned content.
- **Subsection content** — new `GetWikipediaSectionWithSubsectionsAsync` fetches a section and all its nested subsections as a structured list of `SectionContent` objects.
- **Multi-value property constraints** — `PropertyConstraint` now supports a `Values` property for matching against entities with multiple values (e.g., multiple authors).
- **Child entity discovery** — new `GetChildEntitiesAsync` traverses parent-child relationships generically. Supports forward and reverse (`^P179`) traversal, optional P31 type filtering, and automatic ordering.

## v0.9.0

- **Public EntityLabel setter** — `WikidataValue.EntityLabel` is now a public setter.

## v0.8.0

- **Automatic entity label resolution in GetPropertiesAsync** — labels are batch-fetched and respect the language parameter with fallback.

## v0.7.0

- **Entity label resolution for GetPropertiesAsync** — new `resolveEntityLabels` parameter.

## v0.6.0

- **Type-filtered search** — CirrusSearch `haswbstatement:P31=QID` at query time. Multi-type OR logic. Per-request `TypeHierarchyDepth` override.
- **Multi-language reconciliation** — concurrent search in multiple languages, deduplicated by QID.
- **Entity label resolution** — `GetEntitiesAsync(qids, resolveEntityLabels: true)`.
- **Work-to-edition pivoting** — `GetEditionsAsync` and `GetWorkForEditionAsync`.
- **Diacritic-aware search** — `DiacriticInsensitive` flag.
- **Display-friendly labels** — `IncludeSitelinkLabels` option.
- **Wikipedia summary language fallback**.
- **Query pre-cleaning** — `Cleaners` pipeline.
- **Pseudonym detection** — `GetAuthorPseudonymsAsync`.
- **Caching infrastructure** — `CachingDelegatingHandler` abstract base class.

## v0.5.0

- **Wikipedia section content** — `GetWikipediaSectionsAsync` and `GetWikipediaSectionContentAsync`.
- **Staleness detection** — `LastRevisionId`, `Modified`, and `GetRevisionIdsAsync`.

## v0.4.0

- **Cross-language label scoring** — scorer compares against labels in all languages.
- **MatchedLabel property** on results.

## v0.3.0

- **External ID lookup**, **value formatting**, **property labels**, **entity images**, **Wikipedia summaries**.
- **W3C Reconciliation API** — ASP.NET Core middleware.
- **Entity change monitoring**, **maxlag support**.

## v0.2.0

- **Data extension**, **qualifiers**, **P279 subclass matching**, **specific property fetching**.
- **Wikipedia URLs**, **batch reconciliation**, **exclude types**, **custom Wikibase support**.

## v0.1.0

- **Core reconciliation** — dual search, fuzzy matching, type filtering, property constraints, property paths, score breakdown, unique ID shortcut, streaming batch, suggest, retry with backoff.
- **Zero dependencies**, **AOT compatible**.
