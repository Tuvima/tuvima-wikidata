namespace Tuvima.Wikidata.Tests;

/// <summary>
/// A very common title ("Drive") has many Wikidata items with the same label, so the text search cannot tell them apart by the title
/// alone; the creator and series the caller already knows must decide, and a novella must count as a book.
/// </summary>
public class BridgeCommonTitleTests
{
    private const string TargetQid = "Q95736256";

    [Fact]
    public async Task ResolveAsync_TextFallbackPicksTheNovellaWhoseAuthorMatchesAmongManySameTitledBooks()
    {
        // Eight other books are also called "Drive"; the right one has the highest id, so a title-only search puts it last.
        var otherQids = Enumerable.Range(1, 8).Select(i => $"Q{i}").ToArray();
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (uri.Contains("action=wbsearchentities", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.SearchResponse(
                    otherQids.Select(q => (q, "Drive")).Append((TargetQid, "Drive")).ToArray())));

            if (uri.Contains("action=query", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("list=search", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.QueryResponse(otherQids.Append(TargetQid).ToArray())));

            if (uri.Contains("action=wbgetentities", StringComparison.OrdinalIgnoreCase))
            {
                var ids = uri.Split("ids=")[1].Split('&')[0].Split('|');
                var entities = ids.Select(id => id switch
                {
                    TargetQid => TestPayloads.Entity(id, "Drive", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q149537"), "normal"),
                        ("P50", "wikibase-item", TestPayloads.ItemDataValue("Q9000"), "normal"))),
                    "Q9000" => TestPayloads.Entity(id, "James S. A. Corey"),
                    "Q9001" => TestPayloads.Entity(id, "Somebody Else"),
                    _ => TestPayloads.Entity(id, "Drive", claims: TestPayloads.Claims(
                        ("P31", "wikibase-item", TestPayloads.ItemDataValue("Q7725634"), "normal"),
                        ("P50", "wikibase-item", TestPayloads.ItemDataValue("Q9001"), "normal"))),
                }).ToArray();
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(entities)));
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var reconciler = TestPayloads.CreateReconciler(handler);

        var result = await reconciler.Bridge.ResolveAsync(new BridgeResolutionRequest
        {
            CorrelationKey = "drive-corey",
            MediaKind = BridgeMediaKind.Book,
            Title = "Drive",
            Creator = "James S. A. Corey",
        });

        Assert.True(result.Found);
        Assert.Equal(BridgeResolutionStrategy.TextSearch, result.MatchedBy);
        Assert.Equal(TargetQid, result.SelectedCandidate?.Qid);
        Assert.Contains("creator.strong", result.SelectedCandidate?.ReasonCodes ?? []);
    }
}
