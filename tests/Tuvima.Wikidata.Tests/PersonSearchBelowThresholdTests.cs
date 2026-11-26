using Tuvima.Wikidata;
using Xunit;

namespace Tuvima.Wikidata.Tests;

public sealed class PersonSearchBelowThresholdTests
{
    [Fact]
    public async Task SearchAsync_HitBelowTheThreshold_IsNotFoundAndFetchesNoExtraDetails()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    TestPayloads.SearchResponse(("Q9", "Zed Unrelated"))));
            }

            if (uri.Contains("action=query&list=search", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse("Q9")));
            }

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q9", "Zed Unrelated", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q5"), "normal"))))));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Persons.SearchAsync(new PersonSearchRequest
        {
            Name = "Alex Author",
            Role = PersonRole.Author,
        });

        Assert.False(result.Found);
        Assert.Equal("Q9", result.Qid);
        Assert.Empty(result.Occupations);
        var entityFetches = handler.RequestedUris.Count(uri =>
            Uri.UnescapeDataString(uri).Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase));
        Assert.True(entityFetches <= 1, $"Expected at most the scoring fetch, saw {entityFetches}.");
    }
}
