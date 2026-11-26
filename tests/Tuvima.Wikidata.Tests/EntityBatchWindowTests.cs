using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Tests;

public class EntityBatchWindowTests
{
    private static (WikidataEntityFetcher Fetcher, TestHttpMessageHandler Handler, ResilientHttpClient Pipeline, HttpClient Http) Create(
        TimeSpan window,
        Func<string[], Task<HttpResponseMessage>>? respond = null)
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var ids = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["ids"]!.Split('|');
            return respond is not null
                ? respond(ids)
                : Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                    ids.Select(id => TestPayloads.Entity(id, $"Label {id}")).ToArray())));
        });
        var http = new HttpClient(handler);
        var baseOptions = Phase1HttpReliabilityTests.Options();
        var options = new WikidataReconcilerOptions
        {
            MaxRetries = baseOptions.MaxRetries,
            Timeout = baseOptions.Timeout,
            RetryJitterRatio = 0,
            WikidataRateLimit = baseOptions.WikidataRateLimit,
            DefaultRateLimit = baseOptions.DefaultRateLimit,
            EntityBatchWindow = window,
        };
        var pipeline = new ResilientHttpClient(http, options, new());
        return (new WikidataEntityFetcher(pipeline, options, new()), handler, pipeline, http);
    }

    private static string[] RequestedIds(TestHttpMessageHandler handler, int index) =>
        System.Web.HttpUtility.ParseQueryString(new Uri(handler.RequestedUris.ElementAt(index)).Query)["ids"]!.Split('|');

    [Fact]
    public async Task CallersAskingAtTheSameTime_ShareOneRequest()
    {
        var (fetcher, handler, pipeline, http) = Create(TimeSpan.FromMilliseconds(200));
        using var _ = http;
        using var __ = pipeline;

        var first = fetcher.FetchEntitiesAsync(["Q1"], "en");
        var second = fetcher.FetchEntitiesAsync(["Q2"], "en");
        var third = fetcher.FetchEntitiesAsync(["Q3", "Q1"], "en");
        var results = await Task.WhenAll(first, second, third);

        Assert.Single(handler.RequestedUris);
        Assert.Equal(["Q1", "Q2", "Q3"], RequestedIds(handler, 0));
        Assert.Single(results[0]);
        Assert.Single(results[1]);
        Assert.Equal(2, results[2].Count);
        Assert.Equal("Label Q2", results[1]["Q2"].Labels!["en"].Value);
    }

    [Fact]
    public async Task LabelsOnly_IsNotMixedIntoTheClaimsRequest_ButClaimsInAnyLanguageShareOne()
    {
        var (fetcher, handler, pipeline, http) = Create(TimeSpan.FromMilliseconds(200));
        using var _ = http;
        using var __ = pipeline;

        await Task.WhenAll(
            fetcher.FetchEntitiesAsync(["Q1"], "en"),
            fetcher.FetchLabelsOnlyAsync(["Q2"], "en"),
            fetcher.FetchEntitiesAsync(["Q3"], "de"));

        // Claims asked for in different languages are fetched once in full; labels-only stays a lighter request of its own.
        Assert.Equal(2, handler.RequestedUris.Count);
    }

    [Fact]
    public async Task AFullBatch_IsSentWithoutWaitingForTheWindow()
    {
        var (fetcher, handler, pipeline, http) = Create(TimeSpan.FromSeconds(30));
        using var _ = http;
        using var __ = pipeline;
        var ids = Enumerable.Range(1, 50).Select(number => $"Q{number}").ToList();

        var result = await fetcher.FetchEntitiesAsync(ids, "en").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(50, result.Count);
        Assert.Single(handler.RequestedUris);
    }

    [Fact]
    public async Task ALoneCaller_WaitsOnlyTheWindow()
    {
        var (fetcher, handler, pipeline, http) = Create(TimeSpan.FromMilliseconds(50));
        using var _ = http;
        using var __ = pipeline;

        var result = await fetcher.FetchEntitiesAsync(["Q9"], "en").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(result);
        Assert.Single(handler.RequestedUris);
    }

    [Fact]
    public async Task WindowOfZero_KeepsOneRequestPerCaller()
    {
        var (fetcher, handler, pipeline, http) = Create(TimeSpan.Zero);
        using var _ = http;
        using var __ = pipeline;

        await Task.WhenAll(fetcher.FetchEntitiesAsync(["Q1"], "en"), fetcher.FetchEntitiesAsync(["Q2"], "en"));

        Assert.Equal(2, handler.RequestedUris.Count);
    }

    [Fact]
    public async Task AFailedSharedRequest_FailsEveryCallerThatWaitedOnIt()
    {
        var (fetcher, handler, pipeline, http) = Create(
            TimeSpan.FromMilliseconds(100),
            _ => Task.FromResult(TestHttpMessageHandler.Json("{\"error\":{\"code\":\"readonly\",\"info\":\"x\"}}", System.Net.HttpStatusCode.BadRequest)));
        using var _ = http;
        using var __ = pipeline;

        var first = fetcher.FetchEntitiesAsync(["Q1"], "en");
        var second = fetcher.FetchEntitiesAsync(["Q2"], "en");

        await Assert.ThrowsAnyAsync<Exception>(() => first);
        await Assert.ThrowsAnyAsync<Exception>(() => second);
        Assert.Single(handler.RequestedUris);
    }

    [Fact]
    public async Task ACallerThatCancels_StopsWaiting_AndOthersStillGetTheirAnswer()
    {
        var (fetcher, _, pipeline, http) = Create(TimeSpan.FromMilliseconds(300));
        using var _ = http;
        using var __ = pipeline;
        using var cts = new CancellationTokenSource();

        var cancelled = fetcher.FetchEntitiesAsync(["Q1"], "en", cts.Token);
        var other = fetcher.FetchEntitiesAsync(["Q2"], "en");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Single(await other);
    }

    [Fact]
    public async Task EntitiesNotAnswered_AreAbsent_NotAnError()
    {
        var (fetcher, _, pipeline, http) = Create(
            TimeSpan.FromMilliseconds(50),
            ids => Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(
                ids.Where(id => id != "Q404").Select(id => TestPayloads.Entity(id, $"Label {id}")).ToArray()))));
        using var _ = http;
        using var __ = pipeline;

        var result = await fetcher.FetchEntitiesAsync(["Q1", "Q404"], "en");

        Assert.Single(result);
        Assert.True(result.ContainsKey("Q1"));
    }

    [Fact]
    public async Task WhenEveryCallerCancels_TheSharedRequestIsCancelled()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestHttpMessageHandler(async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped.TrySetResult(); }
            return TestHttpMessageHandler.Json("{}");
        });
        using var http = new HttpClient(handler);
        var baseOptions = Phase1HttpReliabilityTests.Options();
        var options = new WikidataReconcilerOptions
        {
            MaxRetries = 0,
            Timeout = baseOptions.Timeout,
            WikidataRateLimit = baseOptions.WikidataRateLimit,
            DefaultRateLimit = baseOptions.DefaultRateLimit,
            EntityBatchWindow = TimeSpan.FromMilliseconds(20),
        };
        using var pipeline = new ResilientHttpClient(http, options, new());
        var fetcher = new WikidataEntityFetcher(pipeline, options, new());
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        var one = fetcher.FetchEntitiesAsync(["Q1"], "en", first.Token);
        var two = fetcher.FetchEntitiesAsync(["Q1", "Q2"], "en", second.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await first.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => one);
        Assert.False(stopped.Task.IsCompleted); // the second caller still wants the request

        await second.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => two);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
