using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Tests;

public class SearchCachingTests
{
    [Theory]
    [InlineData("https://www.wikidata.org/w/api.php?action=wbsearchentities&search=Hans+Zimmer&language=en&type=item&format=json", true)]
    [InlineData("https://www.wikidata.org/w/api.php?action=query&list=search&srsearch=Hans+Zimmer&format=json", true)]
    [InlineData("https://www.wikidata.org/w/api.php?action=query&list=search&srsearch=haswbstatement:P31=Q5+Hans+Zimmer&format=json", true)]
    [InlineData("https://www.wikidata.org/w/api.php?action=wbgetentities&ids=Q1&format=json", true)]
    [InlineData("https://en.wikipedia.org/w/api.php?action=parse&page=Dune&prop=tocdata&format=json", true)]
    [InlineData("https://en.wikipedia.org/w/api.php?action=parse&page=Dune&prop=text&section=1&format=json", false)]
    [InlineData("https://www.wikidata.org/w/api.php?action=query&list=recentchanges&format=json", false)]
    public void IsCacheable_CoversSearchesAndTableOfContentsButNotLiveFeeds(string url, bool expected)
        => Assert.Equal(expected, ProviderRequest.Create(url).IsCacheable());

    [Fact]
    public async Task RepeatedSearch_IsAskedOnce()
    {
        var calls = 0;
        using var client = new HttpClient(new TestHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TestHttpMessageHandler.Json("""{"success":1,"search":[]}"""));
        }));
        using var pipeline = new ResilientHttpClient(client, new WikidataReconcilerOptions(), new());
        const string url = "https://www.wikidata.org/w/api.php?action=wbsearchentities&search=Nobody&language=en&type=item&format=json";

        await pipeline.GetStringAsync(url, default);
        await pipeline.GetStringAsync(url, default);

        Assert.Equal(1, calls);
    }
}
