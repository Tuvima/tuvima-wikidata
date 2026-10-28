namespace Tuvima.Wikidata.Tests;

public class BridgeResolutionServiceTests
{
    [Fact]
    public async Task ResolveBatchStreamAsync_YieldsResultsAndProgressEvents()
    {
        var events = new List<WikidataProgressEvent>();
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("haswbstatement:P345=tt0903747", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("Q1")));

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=Q1", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "Breaking Bad", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q5398426"), "normal"),
                        ("P345", "external-id", TestPayloads.StringDataValue("tt0903747"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = new WikidataReconciler(
            TestPayloads.CreateHttpClient(handler),
            new WikidataReconcilerOptions
            {
                UserAgent = "Tuvima.Wikidata.Tests/3.1 (https://github.com/Tuvima/wikidata)",
                EnableResponseCaching = false,
                WikidataRateLimit = ProviderRateLimitOptions.Unthrottled,
                WikipediaRateLimit = ProviderRateLimitOptions.Unthrottled,
                CommonsRateLimit = ProviderRateLimitOptions.Unthrottled,
                DefaultRateLimit = ProviderRateLimitOptions.Unthrottled,
                ProgressReporter = events.Add
            });

        var results = new List<BridgeResolutionResult>();
        await foreach (var result in reconciler.Bridge.ResolveBatchStreamAsync([
            new BridgeResolutionRequest
            {
                CorrelationKey = "show",
                MediaKind = BridgeMediaKind.TvSeries,
                BridgeIds = new Dictionary<string, string> { ["imdb_id"] = "tt0903747" }
            }
        ]))
        {
            results.Add(result);
        }

        var resolved = Assert.Single(results);
        Assert.True(resolved.Found);
        Assert.Equal("show", resolved.CorrelationKey);
        Assert.Equal("Q1", resolved.SelectedCandidate?.Qid);
        Assert.Contains(events, e => e.Phase == WikidataProgressPhases.Planned);
        Assert.Contains(events, e => e.Phase == WikidataProgressPhases.ExternalIdLookup);
        Assert.Contains(events, e => e.Phase == WikidataProgressPhases.EntityFetch);
        Assert.Contains(events, e => e.Phase == WikidataProgressPhases.Completed && e.CorrelationKey == "show");
        Assert.Equal(1, resolved.Diagnostics.DistinctLookupCount);
        Assert.Equal(1, resolved.Diagnostics.FetchedEntityCount);
        Assert.Equal(WikidataProgressPhases.Completed, resolved.Diagnostics.CompletedPhase);
    }

    [Fact]
    public void WikidataLibraryInfo_ExposesPackageVersion()
    {
        Assert.StartsWith("3.10.1", WikidataLibraryInfo.PackageVersion, StringComparison.Ordinal);
        Assert.StartsWith("3.10.1", WikidataReconciler.LibraryVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveBatchAsync_DeduplicatesDuplicateBridgeLookups()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("haswbstatement:P345=tt0903747", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("Q1")));

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=Q1", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "Breaking Bad", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q5398426"), "normal"),
                        ("P345", "external-id", TestPayloads.StringDataValue("tt0903747"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var results = await reconciler.Bridge.ResolveBatchAsync([
            new BridgeResolutionRequest
            {
                CorrelationKey = "a",
                MediaKind = BridgeMediaKind.TvSeries,
                BridgeIds = new Dictionary<string, string> { ["imdb_id"] = "tt0903747" }
            },
            new BridgeResolutionRequest
            {
                CorrelationKey = "b",
                MediaKind = BridgeMediaKind.TvSeries,
                BridgeIds = new Dictionary<string, string> { ["imdb_id"] = "tt0903747" }
            }
        ]);

        Assert.Equal("Q1", results["a"].SelectedCandidate?.Qid);
        Assert.Equal("Q1", results["b"].SelectedCandidate?.Qid);
        Assert.Equal(1, handler.RequestedUris.Count(uri =>
            Uri.UnescapeDataString(uri).Contains("haswbstatement:P345=tt0903747", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ResolveAsync_RanksByMediaTypeAndTitle()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("haswbstatement:P345=tt0903747", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("Q2", "Q1")));

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                (uri.Contains("ids=Q1|Q2", StringComparison.OrdinalIgnoreCase) ||
                 uri.Contains("ids=Q2|Q1", StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "Breaking Bad", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q5398426"), "normal"),
                        ("P345", "external-id", TestPayloads.StringDataValue("tt0903747"), "normal"))),
                    TestPayloads.Entity("Q2", "Breaking Bad episode", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q21191270"), "normal"),
                        ("P345", "external-id", TestPayloads.StringDataValue("tt0903747"), "normal"))))));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                (uri.Contains("ids=Q1", StringComparison.OrdinalIgnoreCase) ||
                 uri.Contains("ids=Q2", StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "Breaking Bad", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q5398426"), "normal"),
                        ("P345", "external-id", TestPayloads.StringDataValue("tt0903747"), "normal"))),
                    TestPayloads.Entity("Q2", "Breaking Bad episode", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q21191270"), "normal"),
                        ("P345", "external-id", TestPayloads.StringDataValue("tt0903747"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "show",
            MediaKind = BridgeMediaKind.TvSeries,
            Title = "Breaking Bad",
            BridgeIds = new Dictionary<string, string> { ["imdb_id"] = "tt0903747" }
        });

        Assert.True(result.Found);
        Assert.Equal(BridgeResolutionStrategy.BridgeId, result.MatchedBy);
        Assert.Equal("Q1", result.SelectedCandidate?.Qid);
        Assert.Equal("P345", result.SelectedCandidate?.MatchedPropertyId);
        Assert.Contains("type.match", result.SelectedCandidate?.ReasonCodes ?? []);
    }

    [Fact]
    public async Task ResolveAsync_RanksTvSeasonBySeasonNumber()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("haswbstatement:P6381=456", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("Q1", "Q2")));

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "Show season 1", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q3464665"), "normal"),
                        ("P6381", "external-id", TestPayloads.StringDataValue("456"), "normal"),
                        ("P4908", "string", TestPayloads.StringDataValue("1"), "normal"))),
                    TestPayloads.Entity("Q2", "Show season 2", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q3464665"), "normal"),
                        ("P6381", "external-id", TestPayloads.StringDataValue("456"), "normal"),
                        ("P4908", "string", TestPayloads.StringDataValue("2"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "season",
            MediaKind = BridgeMediaKind.TvSeason,
            SeasonNumber = 2,
            BridgeIds = new Dictionary<string, string> { ["itunes_tv_season_id"] = "456" }
        });

        Assert.True(result.Found);
        Assert.Equal("Q2", result.SelectedCandidate?.Qid);
        Assert.Contains("season.ordinal.match", result.SelectedCandidate?.ReasonCodes ?? []);
        Assert.Contains("season.ordinal.mismatch", result.Candidates[1].Warnings);
    }

    [Fact]
    public async Task ResolveAsync_RanksTvEpisodeByEpisodeNumber()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("haswbstatement:P7043=123", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("Q1", "Q2")));

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "Episode 1", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q21191270"), "normal"),
                        ("P7043", "external-id", TestPayloads.StringDataValue("123"), "normal"),
                        ("P1545", "string", TestPayloads.StringDataValue("1"), "normal"))),
                    TestPayloads.Entity("Q2", "Episode 2", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q21191270"), "normal"),
                        ("P7043", "external-id", TestPayloads.StringDataValue("123"), "normal"),
                        ("P1545", "string", TestPayloads.StringDataValue("2"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "episode",
            MediaKind = BridgeMediaKind.TvEpisode,
            EpisodeNumber = 2,
            BridgeIds = new Dictionary<string, string> { ["tvdb_episode_id"] = "123" }
        });

        Assert.True(result.Found);
        Assert.Equal("Q2", result.SelectedCandidate?.Qid);
        Assert.Contains("episode.ordinal.match", result.SelectedCandidate?.ReasonCodes ?? []);
        Assert.Contains("episode.ordinal.mismatch", result.Candidates[1].Warnings);
    }

    [Fact]
    public async Task ResolveAsync_RanksComicIssueByIssueNumber()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("haswbstatement:P5905=789", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("Q1", "Q2")));

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "Comic issue 11", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q1114461"), "normal"),
                        ("P5905", "external-id", TestPayloads.StringDataValue("789"), "normal"),
                        ("P433", "string", TestPayloads.StringDataValue("11"), "normal"))),
                    TestPayloads.Entity("Q2", "Comic issue 12", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q1114461"), "normal"),
                        ("P5905", "external-id", TestPayloads.StringDataValue("789"), "normal"),
                        ("P433", "string", TestPayloads.StringDataValue("12"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "comic",
            MediaKind = BridgeMediaKind.ComicIssue,
            IssueNumber = "12",
            BridgeIds = new Dictionary<string, string> { ["comicvine_id"] = "789" }
        });

        Assert.True(result.Found);
        Assert.Equal("Q2", result.SelectedCandidate?.Qid);
        Assert.Contains("issue.ordinal.match", result.SelectedCandidate?.ReasonCodes ?? []);
        Assert.Contains("issue.ordinal.mismatch", result.Candidates[1].Warnings);
    }

    [Fact]
    public async Task ResolveAsync_RollsEditionToCanonicalWork()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("haswbstatement:P212=9780441172719", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("QEdition")));

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=QEdition", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("QEdition", "Dune paperback", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q3331189"), "normal"),
                        ("P212", "external-id", TestPayloads.StringDataValue("9780441172719"), "normal"),
                        ("P629", "wikibase-item", TestPayloads.ItemDataValue("QWork"), "normal"))))));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase) &&
                uri.Contains("ids=QWork", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("QWork", "Dune", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q7725634"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "book",
            MediaKind = BridgeMediaKind.Book,
            BridgeIds = new Dictionary<string, string> { ["isbn13"] = "978-0-441-17271-9" }
        });

        Assert.True(result.Found);
        Assert.Equal("QEdition", result.SelectedCandidate?.Qid);
        Assert.Equal("QWork", result.CanonicalWorkQid);
        Assert.Equal("P629", result.Rollup?.RelationshipPath.Single().PropertyId);
    }

    [Fact]
    public async Task ResolveAsync_ClassifiesP361ListAsDiagnosticNotImmediateSeries()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("haswbstatement:P345=tt4633694", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("QFilm")));

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            {
                var entities = new[]
                {
                    TestPayloads.Entity("QFilm", "Spider-Man: Into the Spider-Verse", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q11424"), "normal"),
                        ("P345", "external-id", TestPayloads.StringDataValue("tt4633694"), "normal"),
                        ("P361", "wikibase-item", TestPayloads.ItemDataValue("Q65071834"), "normal"))),
                    TestPayloads.Entity("Q65071834", "list of Sony Pictures Animation productions", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q13406463"), "normal")))
                };

                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(entities)));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "movie",
            MediaKind = BridgeMediaKind.Movie,
            BridgeIds = new Dictionary<string, string> { ["imdb_id"] = "tt4633694" }
        });

        var series = Assert.Single(result.Series);
        Assert.Equal("Q65071834", series.SeriesQid);
        Assert.Equal(WikidataContainerKind.PublisherOrProductionList, series.ContainerKind);
        Assert.False(series.IsImmediateSeries);
    }

    [Fact]
    public async Task ResolveAsync_ClassifiesP179FilmSeriesAsImmediateSeries()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("haswbstatement:P345=tt4633694", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("QFilm")));

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            {
                var entities = new[]
                {
                    TestPayloads.Entity("QFilm", "Spider-Man: Into the Spider-Verse", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q11424"), "normal"),
                        ("P345", "external-id", TestPayloads.StringDataValue("tt4633694"), "normal"),
                        ("P179", "wikibase-item", TestPayloads.ItemDataValue("Q99601314"), "normal"))),
                    TestPayloads.Entity("Q99601314", "Spider-Verse", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q24856"), "normal")))
                };

                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(entities)));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "movie",
            MediaKind = BridgeMediaKind.Movie,
            BridgeIds = new Dictionary<string, string> { ["imdb_id"] = "tt4633694" }
        });

        var series = Assert.Single(result.Series);
        Assert.Equal("Q99601314", series.SeriesQid);
        Assert.Equal(WikidataContainerKind.OrderedSeries, series.ContainerKind);
        Assert.True(series.IsImmediateSeries);
    }

    [Fact]
    public async Task ResolveAsync_UnknownP179ContainerWithoutOrderingEvidence_IsNotImmediate()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("haswbstatement:P345=tt0000001", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("QWork")));

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            {
                var entities = new[]
                {
                    TestPayloads.Entity("QWork", "A work", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q11424"), "normal"),
                        ("P345", "external-id", TestPayloads.StringDataValue("tt0000001"), "normal"),
                        ("P179", "wikibase-item", TestPayloads.ItemDataValue("QBroad"), "normal"))),
                    TestPayloads.Entity("QBroad", "Broad context")
                };

                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(entities)));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "work",
            MediaKind = BridgeMediaKind.Movie,
            BridgeIds = new Dictionary<string, string> { ["imdb_id"] = "tt0000001" }
        });

        var series = Assert.Single(result.Series);
        Assert.Equal(WikidataContainerKind.Unknown, series.ContainerKind);
        Assert.False(series.IsImmediateSeries);
    }

    [Fact]
    public async Task ResolveAsync_TextFallbackAcceptsMangaSeriesTypeHint()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("action=query", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("list=search", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("Akira", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("Q91486")));
            }

            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("Akira", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.SearchResponse(("Q91486", "Akira"))));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("ids=Q91486", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q91486", "Akira", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q21198342"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "akira",
            MediaKind = BridgeMediaKind.ComicSeries,
            Title = "Akira"
        });

        Assert.True(result.Found);
        Assert.Equal(BridgeResolutionStrategy.TextSearch, result.MatchedBy);
        Assert.Equal("Q91486", result.SelectedCandidate?.Qid);
        Assert.Contains("type.match", result.SelectedCandidate?.ReasonCodes ?? []);
    }

    [Fact]
    public async Task ResolveAsync_TextFallbackFindsAmbiguousComicSeriesWithTypedSearch()
    {
        var typedSearchOrder = new List<string>();
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("action=query", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("list=search", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("Batman", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("haswbstatement:P31=Q14406742", StringComparison.OrdinalIgnoreCase))
            {
                typedSearchOrder.Add("Q14406742");
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("Q2633138")));
            }

            if (uri.Contains("action=query", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("list=search", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("Batman", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("haswbstatement:P31=Q1004", StringComparison.OrdinalIgnoreCase))
            {
                typedSearchOrder.Add("Q1004");
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("Q4869415")));
            }

            if (uri.Contains("action=query", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("list=search", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("Batman", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse()));
            }

            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("Batman", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.SearchResponse()));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("ids=Q2633138", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("Q4869415", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q2633138", "Batman", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q14406742"), "normal"))),
                    TestPayloads.Entity("Q4869415", "Batman: Gotham Knights", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q1004"), "normal"))))));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("ids=Q2633138", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q2633138", "Batman", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q14406742"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "batman-405",
            MediaKind = BridgeMediaKind.ComicSeries,
            Title = "Batman",
            SeriesTitle = "Batman",
            IssueNumber = "405"
        });

        Assert.True(result.Found);
        Assert.Equal(BridgeResolutionStrategy.TextSearch, result.MatchedBy);
        Assert.Equal("Q2633138", result.SelectedCandidate?.Qid);
        Assert.Contains("type.match", result.SelectedCandidate?.ReasonCodes ?? []);
        Assert.True(
            typedSearchOrder.IndexOf("Q14406742") >= 0
            && typedSearchOrder.IndexOf("Q14406742") < typedSearchOrder.IndexOf("Q1004"),
            "Comic-series typed search should run before generic comics search.");
    }

    [Fact]
    public async Task ResolveAsync_TextFallbackAcceptsGraphicNovelComicSeriesType()
    {
        var typedSearches = new List<string>();
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("action=query", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("list=search", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("Watchmen", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("haswbstatement:P31=Q3297186", StringComparison.OrdinalIgnoreCase))
            {
                typedSearches.Add("Q3297186");
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("Q128444")));
            }

            if (uri.Contains("action=query", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("list=search", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("Watchmen", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse()));
            }

            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("Watchmen", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.SearchResponse()));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("ids=Q128444", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q128444", "Watchmen", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q3297186"), "normal"),
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q7725634"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "watchmen-1",
            MediaKind = BridgeMediaKind.ComicSeries,
            Title = "Watchmen",
            SeriesTitle = "Watchmen",
            IssueNumber = "1"
        });

        Assert.True(result.Found);
        Assert.Equal("Q128444", result.SelectedCandidate?.Qid);
        Assert.Contains("Q3297186", typedSearches);
        Assert.Contains("type.match", result.SelectedCandidate?.ReasonCodes ?? []);
    }

    [Fact]
    public async Task WikipediaSummaryResults_ReturnsCleanNoSitelinkResult()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "No Page"))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var results = await reconciler.Wikipedia.GetWikipediaSummaryResultsAsync(["Q1"]);

        Assert.False(results["Q1"].Found);
        Assert.Equal(WikidataFailureKind.NoSitelink, results["Q1"].FailureKind);
    }
}
