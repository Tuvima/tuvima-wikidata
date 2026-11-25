using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Tests;

public class EntityMemoryTests
{
    private static (WikidataEntityFetcher Fetcher, TestHttpMessageHandler Handler, ResilientHttpClient Pipeline, HttpClient Http) Create()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var ids = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["ids"]!.Split('|');
            return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                ids.Select(id => TestPayloads.Entity(id, $"Label {id}", sitelinks: TestPayloads.Sitelinks(("enwiki", id)))).ToArray())));
        });
        var http = new HttpClient(handler);
        var options = Phase1HttpReliabilityTests.Options();
        var pipeline = new ResilientHttpClient(http, options, new());
        return (new WikidataEntityFetcher(pipeline, options, new()), handler, pipeline, http);
    }

    [Fact]
    public async Task LabelsAskedAfterFullEntity_AreNotFetchedAgain()
    {
        var (fetcher, handler, pipeline, http) = Create();
        using var _ = http;
        using var __ = pipeline;

        await fetcher.FetchEntitiesAsync(["Q1", "Q2"], "en");
        var labels = await fetcher.FetchLabelsOnlyAsync(["Q1", "Q2"], "en");

        Assert.Single(handler.RequestedUris);
        Assert.Equal(2, labels.Count);
    }

    [Fact]
    public async Task OnlyTheEntitiesNotYetHeld_AreAskedFor()
    {
        var (fetcher, handler, pipeline, http) = Create();
        using var _ = http;
        using var __ = pipeline;

        await fetcher.FetchEntitiesAsync(["Q1"], "en");
        var both = await fetcher.FetchEntitiesAsync(["Q1", "Q2"], "en");

        Assert.Equal(2, handler.RequestedUris.Count);
        var second = System.Web.HttpUtility.ParseQueryString(new Uri(handler.RequestedUris.Last()).Query)["ids"];
        Assert.Equal("Q2", second);
        Assert.Equal(2, both.Count);
    }

    [Fact]
    public async Task SitelinksAskedAfterAllLanguageEntityWithSitelinks_AreNotFetchedAgain()
    {
        var (fetcher, handler, pipeline, http) = Create();
        using var _ = http;
        using var __ = pipeline;

        await fetcher.FetchEntitiesAllLanguagesAsync(["Q7"], includeSitelinks: true);
        await fetcher.FetchEntitiesWithSitelinksAsync(["Q7"], "en");

        Assert.Single(handler.RequestedUris);
    }

    [Fact]
    public async Task RicherPropertiesThanHeld_AreStillFetched()
    {
        var (fetcher, handler, pipeline, http) = Create();
        using var _ = http;
        using var __ = pipeline;

        await fetcher.FetchLabelsOnlyAsync(["Q3"], "en");
        await fetcher.FetchEntitiesAsync(["Q3"], "en");

        Assert.Equal(2, handler.RequestedUris.Count);
    }

    [Fact]
    public async Task OtherLanguage_IsNotServedFromALanguageSpecificEntity()
    {
        var (fetcher, handler, pipeline, http) = Create();
        using var _ = http;
        using var __ = pipeline;

        await fetcher.FetchLabelsOnlyAsync(["Q4"], "en");
        await fetcher.FetchLabelsOnlyAsync(["Q4"], "de");

        Assert.Equal(2, handler.RequestedUris.Count);
    }
}
