using Tuvima.Wikidata.Internal;
using Xunit;

namespace Tuvima.Wikidata.Tests;

public sealed class RequestCutTests
{
    private static TestHttpMessageHandler PersonHandler() => new((request, _) =>
    {
        var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());
        if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.SearchResponse(("Q7", "Alex Actor"))));
        if (uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("Q7")));
        if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                TestPayloads.Entity("Q7", "Alex Actor", claims: TestPayloads.Claims(
                    ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q5"), "normal"))))));
        throw new InvalidOperationException($"Unexpected request: {uri}");
    });

    private static int Searches(TestHttpMessageHandler handler, string kind) =>
        handler.RequestedUris.Count(uri => Uri.UnescapeDataString(uri).Contains(kind, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task PersonSearch_SkipsThePlainFullTextSearch_AndUsesTheTypeFilteredOne()
    {
        var handler = PersonHandler();
        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Persons.SearchAsync(new PersonSearchRequest { Name = "Alex Actor", Role = PersonRole.Unknown });

        Assert.Equal("Q7", result.Qid);
        Assert.Equal(1, Searches(handler, "action=wbsearchentities"));
        // One full-text search only, and it is the type-filtered one.
        Assert.Equal(1, Searches(handler, "list=search"));
        Assert.Equal(1, Searches(handler, "haswbstatement:P31=Q5"));
    }

    [Fact]
    public async Task PersonSearch_ForAMusicalGroupRole_AsksAllAllowedTypesInOneRequest()
    {
        var handler = PersonHandler();
        using var reconciler = TestPayloads.CreateReconciler(handler);

        await reconciler.Persons.SearchAsync(new PersonSearchRequest { Name = "Alex Actor", Role = PersonRole.Performer });

        Assert.Equal(1, Searches(handler, "list=search"));
        Assert.Equal(1, Searches(handler, "haswbstatement:P31=Q5"));
        Assert.Equal(1, Searches(handler, "OR haswbstatement:P31="));
    }

    [Fact]
    public async Task OrdinaryReconcile_StillRunsThePlainFullTextSearch()
    {
        var handler = PersonHandler();
        using var reconciler = TestPayloads.CreateReconciler(handler);

        await reconciler.Reconcile.ReconcileAsync(new ReconciliationRequest { Query = "Alex Actor", Types = ["Q5"] });

        Assert.Equal(2, Searches(handler, "list=search"));
    }

    [Fact]
    public async Task ClaimsThenSitelinksThenLanguages_CostOneEntityRequest()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
            Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(TestPayloads.Entity("Q1", "One")))));
        var http = new HttpClient(handler);
        var options = new WikidataReconcilerOptions { EntityBatchWindow = TimeSpan.Zero, WikidataRateLimit = ProviderRateLimitOptions.Unthrottled, DefaultRateLimit = ProviderRateLimitOptions.Unthrottled };
        using var pipeline = new ResilientHttpClient(http, options, new());
        var fetcher = new WikidataEntityFetcher(pipeline, options, new());

        await fetcher.FetchEntitiesAsync(["Q1"], "en");
        await fetcher.FetchEntitiesWithSitelinksAsync(["Q1"], "en");
        await fetcher.FetchEntitiesAllLanguagesAsync(["Q1"], includeSitelinks: true);
        await fetcher.FetchLabelsOnlyAsync(["Q1"], "de");
        await fetcher.FetchClaimsOnlyAsync(["Q1"]);

        Assert.Single(handler.RequestedUris);
    }

    [Fact]
    public async Task WithWideningOff_EachKindOfDataIsStillFetchedOnItsOwn()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
            Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(TestPayloads.Entity("Q1", "One")))));
        var http = new HttpClient(handler);
        var options = new WikidataReconcilerOptions { EntityBatchWindow = TimeSpan.Zero, WidenEntityFetches = false, WikidataRateLimit = ProviderRateLimitOptions.Unthrottled, DefaultRateLimit = ProviderRateLimitOptions.Unthrottled };
        using var pipeline = new ResilientHttpClient(http, options, new());
        var fetcher = new WikidataEntityFetcher(pipeline, options, new());

        await fetcher.FetchEntitiesAsync(["Q1"], "en");
        await fetcher.FetchEntitiesWithSitelinksAsync(["Q1"], "en");

        Assert.Equal(2, handler.RequestedUris.Count);
    }
}
