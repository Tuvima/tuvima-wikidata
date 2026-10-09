using System.Net;
using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Tests;

public class ScoringImprovementsTests
{
    [Fact]
    public async Task ReconcileAsync_NameValuedItemConstraint_MatchesReferencedItemLabels()
    {
        var labelRequests = 0;
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
                return Json(TestPayloads.SearchResponse(("Q1", "Good Omens"), ("Q2", "Good Omens")));
            if (uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase))
                return Json(TestPayloads.QueryResponse("Q1", "Q2"));
            if (uri.Contains("ids=Q1|Q2", StringComparison.OrdinalIgnoreCase))
            {
                return Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "Good Omens", claims: TestPayloads.Claims(
                        ("P50", "wikibase-item", TestPayloads.ItemDataValue("Q500"), "normal"))),
                    TestPayloads.Entity("Q2", "Good Omens", claims: TestPayloads.Claims(
                        ("P50", "wikibase-item", TestPayloads.ItemDataValue("Q210059"), "normal"),
                        ("P50", "wikibase-item", TestPayloads.ItemDataValue("Q46248"), "normal")))));
            }
            if (uri.Contains("ids=Q210059|Q46248|Q500", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref labelRequests);
                return Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q210059", "Neil Gaiman"),
                    TestPayloads.Entity("Q46248", "Terry Pratchett"),
                    TestPayloads.Entity("Q500", "Somebody Else")));
            }
            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);
        var results = await reconciler.Reconcile.ReconcileAsync(new ReconciliationRequest
        {
            Query = "Good Omens",
            Properties = [new PropertyConstraint("P50", ["Neil Gaiman", "Terry Pratchett"])]
        });

        Assert.Equal("Q2", results[0].Id);
        Assert.Equal(100.0, results[0].Breakdown!.PropertyScores["P50"]);
        Assert.True(results[0].Match);
        Assert.True(results[1].Breakdown!.PropertyScores["P50"] < 50);
        Assert.Equal(1, labelRequests);
    }

    [Fact]
    public async Task ReconcileAsync_QidValuedItemConstraint_DoesNotFetchItemLabels()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
                return Json(TestPayloads.SearchResponse(("Q1", "Good Omens")));
            if (uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase))
                return Json(TestPayloads.QueryResponse("Q1"));
            if (uri.Contains("ids=Q1&", StringComparison.OrdinalIgnoreCase))
            {
                return Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q1", "Good Omens", claims: TestPayloads.Claims(
                        ("P50", "wikibase-item", TestPayloads.ItemDataValue("Q210059"), "normal")))));
            }
            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);
        var results = await reconciler.Reconcile.ReconcileAsync(new ReconciliationRequest
        {
            Query = "Good Omens",
            Properties = [new PropertyConstraint("P50", "Q210059")]
        });

        Assert.Equal(100.0, results[0].Breakdown!.PropertyScores["P50"]);
    }

    [Fact]
    public async Task ReconcileBatchAsync_IdenticalRequests_RunOnceAndPreserveOrder()
    {
        var searches = 0;
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref searches);
                return Json(uri.Contains("search=Dune", StringComparison.Ordinal)
                    ? TestPayloads.SearchResponse(("Q190192", "Dune"))
                    : TestPayloads.SearchResponse(("Q8337", "Emma")));
            }
            if (uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase))
                return Json(TestPayloads.QueryResponse());
            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            {
                return Json(uri.Contains("Q190192", StringComparison.Ordinal)
                    ? TestPayloads.EntityResponse(TestPayloads.Entity("Q190192", "Dune"))
                    : TestPayloads.EntityResponse(TestPayloads.Entity("Q8337", "Emma")));
            }
            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);
        ReconciliationRequest Dune() => new() { Query = "Dune" };
        var requests = new[] { Dune(), new ReconciliationRequest { Query = "Emma" }, Dune(), Dune() };

        var results = await reconciler.Reconcile.ReconcileBatchAsync(requests);

        Assert.Equal(["Q190192", "Q8337", "Q190192", "Q190192"], results.Select(r => r[0].Id));
        Assert.Equal(2, searches);

        var streamed = new List<int>();
        await foreach (var item in reconciler.Reconcile.ReconcileBatchStreamAsync(requests))
            streamed.Add(item.Index);
        Assert.Equal([0, 1, 2, 3], streamed.Order());
    }

    [Fact]
    public void RequestGroups_DifferentOptionsOrCleaners_StaySeparate()
    {
        Func<string, string> cleanerA = s => s.Trim();
        Func<string, string> cleanerB = s => s.ToUpperInvariant();
        var groups = ReconciliationRequestGroups.Create(
        [
            new ReconciliationRequest { Query = "Dune" },
            new ReconciliationRequest { Query = "Dune", Types = ["Q7725634"] },
            new ReconciliationRequest { Query = "Dune", Cleaners = [cleanerA] },
            new ReconciliationRequest { Query = "Dune", Cleaners = [cleanerA] },
            new ReconciliationRequest { Query = "Dune", Cleaners = [cleanerB] },
            new ReconciliationRequest { Query = "Dune", Properties = [new PropertyConstraint("P50", "Frank Herbert")] },
            new ReconciliationRequest { Query = "Dune", Language = "de" },
        ]);

        Assert.Equal(6, groups.Representatives.Count);
        Assert.Equal(groups.GroupOf[2], groups.GroupOf[3]);
    }

    [Theory]
    [InlineData(1984, 1980, 8, true)]   // 1980s
    [InlineData(1979, 1980, 8, false)]
    [InlineData(1950, 1900, 7, true)]   // century stored as first year
    [InlineData(1950, 2000, 7, true)]   // century stored as last year
    [InlineData(1750, 1900, 7, false)]
    public void MatchCoarseYear_ComparesAtStoredPrecision(int query, int entity, int precision, bool expected)
        => Assert.Equal(expected, PropertyMatcher.MatchCoarseYear(query, entity, precision));

    [Fact]
    public async Task ReconcileAsync_DecadePrecisionDate_DoesNotMatchOutsideDecade()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
                return Json(TestPayloads.SearchResponse(("Q1", "Some Film")));
            if (uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase))
                return Json(TestPayloads.QueryResponse("Q1"));
            return Json(TestPayloads.EntityResponse(TestPayloads.Entity("Q1", "Some Film", claims: TestPayloads.Claims(
                ("P577", "time", TestPayloads.TimeDataValue("+1980-00-00T00:00:00Z", precision: 8), "normal")))));
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);
        var inDecade = await reconciler.Reconcile.ReconcileAsync(new ReconciliationRequest
        {
            Query = "Some Film",
            Properties = [new PropertyConstraint("P577", "1984")]
        });
        var outside = await reconciler.Reconcile.ReconcileAsync(new ReconciliationRequest
        {
            Query = "Some Film",
            Properties = [new PropertyConstraint("P577", "2015")]
        });

        Assert.Equal(100.0, inDecade[0].Breakdown!.PropertyScores["P577"]);
        Assert.Equal(0.0, outside[0].Breakdown!.PropertyScores["P577"]);
    }

    [Fact]
    public async Task ReconcileBatch_PerRequestHierarchyDepth_ReusesSubclassCache()
    {
        var subclassFetches = 0;
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
            {
                var id = uri.Contains("search=A&", StringComparison.Ordinal) ? "Q1" : "Q2";
                return Json(TestPayloads.SearchResponse((id, "Novel")));
            }
            if (uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase))
                return Json(TestPayloads.QueryResponse());
            if (uri.Contains("ids=Q8261", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref subclassFetches);
                return Json(TestPayloads.EntityResponse(TestPayloads.Entity("Q8261", "novel", claims: TestPayloads.Claims(
                    ("P279", "wikibase-item", TestPayloads.ItemDataValue("Q7725634"), "normal")))));
            }
            var qid = uri.Contains("ids=Q1", StringComparison.Ordinal) ? "Q1" : "Q2";
            return Json(TestPayloads.EntityResponse(TestPayloads.Entity(qid, "Novel", claims: TestPayloads.Claims(
                ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q8261"), "normal")))));
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);
        var results = await reconciler.Reconcile.ReconcileBatchAsync(
        [
            new ReconciliationRequest { Query = "A", Types = ["Q7725634"], TypeHierarchyDepth = 2 },
            new ReconciliationRequest { Query = "B", Types = ["Q7725634"], TypeHierarchyDepth = 2 },
        ]);

        Assert.All(results, r => Assert.True(r[0].Breakdown!.TypeMatched));
        Assert.Equal(1, subclassFetches);
    }

    [Fact]
    public async Task SuggestTypesAsync_PrefersClasses()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
                return Json(TestPayloads.SearchResponse(("Q208460", "Novel (band)"), ("Q8261", "novel"), ("Q1", "The Novel")));
            if (uri.Contains("props=claims", StringComparison.OrdinalIgnoreCase))
            {
                return Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q208460", "Novel (band)", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q215380"), "normal"))),
                    TestPayloads.Entity("Q8261", "novel", claims: TestPayloads.Claims(
                        ("P279", "wikibase-item", TestPayloads.ItemDataValue("Q7725634"), "normal"))),
                    TestPayloads.Entity("Q1", "The Novel")));
            }
            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);
        var results = await reconciler.Reconcile.SuggestTypesAsync("novel");

        Assert.Equal("Q8261", Assert.Single(results).Id);
    }

    [Fact]
    public async Task SuggestTypesAsync_NoClasses_FallsBackToEntitySuggestions()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
                return Json(TestPayloads.SearchResponse(("Q42", "Douglas Adams")));
            return Json(TestPayloads.EntityResponse(TestPayloads.Entity("Q42", "Douglas Adams")));
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);
        var results = await reconciler.Reconcile.SuggestTypesAsync("douglas");

        Assert.Equal("Q42", Assert.Single(results).Id);
    }

    [Fact]
    public async Task GetEntities_NonexistentIdInBatch_RetriesWithoutIt()
    {
        var requests = new List<string>();
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());
            lock (requests) requests.Add(uri);
            if (uri.Contains("Q999999999", StringComparison.Ordinal))
            {
                return Json("""{"error":{"code":"no-such-entity","info":"Could not find an entity","id":"Q999999999"}}""");
            }
            return Json(TestPayloads.EntityResponse(TestPayloads.Entity("Q42", "Douglas Adams")));
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);
        var entities = await reconciler.Entities.GetEntitiesAsync(["Q42", "Q999999999"]);
        var onlyMissing = await reconciler.Labels.GetAsync("Q999999999");

        Assert.Equal("Douglas Adams", Assert.Single(entities).Value.Label);
        Assert.Null(onlyMissing);
        Assert.Contains(requests, r => r.Contains("ids=Q42&", StringComparison.Ordinal));
    }

#pragma warning disable CS0618 // CachingDelegatingHandler is obsolete but still supported.
    [Fact]
    public async Task CachingDelegatingHandler_DoesNotCacheMediaWikiErrorEnvelopes()
    {
        var calls = 0;
        var inner = new TestHttpMessageHandler((_, _) =>
        {
            calls++;
            return calls == 1
                ? Json("""{"error":{"code":"maxlag","info":"Waiting"}}""")
                : Json("""{"success":1,"entities":{}}""");
        });
        var cache = new DictionaryCachingHandler { InnerHandler = inner };
        using var client = new HttpClient(cache);

        await client.GetStringAsync("https://www.wikidata.org/w/api.php?action=wbgetentities&ids=Q1");
        Assert.Empty(cache.Stored);

        await client.GetStringAsync("https://www.wikidata.org/w/api.php?action=wbgetentities&ids=Q1");
        Assert.Single(cache.Stored);
    }

    [Theory]
    [InlineData("""{"success":1}""", true)]
    [InlineData("[1,2]", true)]
    [InlineData("""{"error":{"code":"maxlag"}}""", false)]
    [InlineData("""{"errors":[]}""", false)]
    [InlineData("<html>Bad gateway</html>", false)]
    public void CachingDelegatingHandler_IsCacheableBody(string body, bool expected)
        => Assert.Equal(expected, CachingDelegatingHandler.IsCacheableBody(body));

    private sealed class DictionaryCachingHandler : CachingDelegatingHandler
    {
        public Dictionary<string, string> Stored { get; } = new();

        protected override Task<string?> GetCachedResponseAsync(string requestUri, CancellationToken cancellationToken)
            => Task.FromResult(Stored.TryGetValue(requestUri, out var value) ? value : null);

        protected override Task CacheResponseAsync(string requestUri, string responseContent, CancellationToken cancellationToken)
        {
            Stored[requestUri] = responseContent;
            return Task.CompletedTask;
        }
    }
#pragma warning restore CS0618

    private static Task<HttpResponseMessage> Json(string json)
        => Task.FromResult(TestHttpMessageHandler.Json(json, HttpStatusCode.OK));
}
