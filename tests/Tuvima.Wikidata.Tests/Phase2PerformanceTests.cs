using Tuvima.Wikidata.Internal;

namespace Tuvima.Wikidata.Tests;

public class Phase2PerformanceTests
{
    private static WikidataResponseCacheKey Key(string key) => new("test", "test", key);

    [Fact]
    public async Task Cache_EvictsLeastRecentlyUsed_AndAccountsForReplacement()
    {
        var cache = new InMemoryWikidataResponseCache(2, 100);
        await cache.SetAsync(Key("a"), "123", TimeSpan.FromHours(1));
        await cache.SetAsync(Key("b"), "123", TimeSpan.FromHours(1));
        Assert.Equal("123", await cache.GetAsync(Key("a")));
        await cache.SetAsync(Key("c"), "123", TimeSpan.FromHours(1));
        Assert.Null(await cache.GetAsync(Key("b")));
        await cache.SetAsync(Key("a"), "x", TimeSpan.FromHours(1));
        Assert.Equal((2, 12L), cache.GetUsage());
    }

    [Fact]
    public async Task Cache_EnforcesByteLimit_AndSkipsOversizedResponses()
    {
        var cache = new InMemoryWikidataResponseCache(10, 12);
        await cache.SetAsync(Key("a"), "123", TimeSpan.FromHours(1));
        await cache.SetAsync(Key("b"), "123", TimeSpan.FromHours(1));
        Assert.Null(await cache.GetAsync(Key("a")));
        await cache.SetAsync(Key("c"), new string('x', 100), TimeSpan.FromHours(1));
        Assert.Null(await cache.GetAsync(Key("c")));
        Assert.Equal((1, 8L), cache.GetUsage());
    }

    [Fact]
    public async Task Cache_PrunesExpiredEntriesOnOtherKeys_AndKeepsFreshReplacement()
    {
        var time = new ManualTime();
        var cache = new InMemoryWikidataResponseCache(5, 100, time);
        await cache.SetAsync(Key("a"), "old", TimeSpan.FromSeconds(1));
        await cache.SetAsync(Key("b"), "old", TimeSpan.FromSeconds(1));
        await cache.SetAsync(Key("a"), "fresh", TimeSpan.FromHours(1));
        time.Advance(TimeSpan.FromSeconds(2));
        await cache.GetAsync(Key("unrelated"));
        Assert.Equal((1, 12L), cache.GetUsage());
        Assert.Equal("fresh", await cache.GetAsync(Key("a")));
        await cache.SetAsync(Key("a"), "disabled", TimeSpan.Zero);
        Assert.Equal((0, 0L), cache.GetUsage());
    }

    [Fact]
    public async Task Cache_ConcurrentUpdatesStayWithinBothLimits()
    {
        var cache = new InMemoryWikidataResponseCache(8, 256);
        await Parallel.ForEachAsync(Enumerable.Range(0, 5000), async (i, token) =>
        {
            await cache.SetAsync(Key((i % 20).ToString()), "response", TimeSpan.FromMinutes(1), token);
            await cache.GetAsync(Key(((i + 1) % 20).ToString()), token);
        });
        var usage = cache.GetUsage();
        Assert.InRange(usage.Count, 1, 8);
        Assert.InRange(usage.SizeBytes, 1, 256);
    }

    [Fact]
    public async Task Cache_RejectsInvalidLimits_AndHonorsCancellation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryWikidataResponseCache(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryWikidataResponseCache(1, 0));
        var cache = new InMemoryWikidataResponseCache();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cache.SetAsync(Key("a"), "value", TimeSpan.FromHours(1), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cache.GetAsync(Key("a"), cancelled.Token));
        Assert.Equal((0, 0L), cache.GetUsage());
    }

    [Fact]
    public async Task Limiter_SpacesAdmissionsAfterConcurrencyBacklog()
    {
        var time = new ManualTime();
        var delays = new List<TimeSpan>();
        using var limiter = new HostRateLimiter(new() { MaxConcurrentRequests = 1, RequestsPerSecond = 1 }, new(), time,
            (delay, _) => { delays.Add(delay); time.Advance(delay); return Task.CompletedTask; });
        var first = await limiter.WaitAsync(default);
        var secondTask = limiter.WaitAsync(default).AsTask();
        var thirdTask = limiter.WaitAsync(default).AsTask();
        time.Advance(TimeSpan.FromSeconds(10));
        first.Dispose();
        // Semaphore acquisition order need not be FIFO.
        var next = await Task.WhenAny(secondTask, thirdTask).WaitAsync(TimeSpan.FromSeconds(3));
        (await next).Dispose();
        var last = ReferenceEquals(next, secondTask) ? thirdTask : secondTask;
        (await last.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(delays));
    }

    [Fact]
    public async Task Limiter_CancelledPacingDoesNotReserveFutureSlotsOrLeakConcurrency()
    {
        var time = new ManualTime();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var limiter = new HostRateLimiter(new() { MaxConcurrentRequests = 1, RequestsPerSecond = 1 }, new(), time,
            async (delay, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    waiting.SetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                else
                {
                    Assert.Equal(TimeSpan.FromSeconds(1), delay);
                    time.Advance(delay);
                }
            });
        (await limiter.WaitAsync(default)).Dispose();
        using var cancelled = new CancellationTokenSource();
        var abandoned = limiter.WaitAsync(cancelled.Token).AsTask();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        (await limiter.WaitAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task BoundedBatch_PreservesInputOrder_AndLimitsStartedWork()
    {
        var releases = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var started = 0;
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = BoundedAsync.SelectAsync(new[] { 0, 1, 2, 3 }, 2, async (i, token) =>
        {
            Interlocked.Increment(ref started);
            if (i == 2) thirdStarted.SetResult();
            return await releases[i].Task.WaitAsync(token);
        }, default);
        Assert.Equal(2, started);
        releases[1].SetResult(11);
        await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(3, started);
        releases[2].SetResult(12);
        releases[3].SetResult(13);
        releases[0].SetResult(10);
        Assert.Equal(new[] { 10, 11, 12, 13 }, await batch.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task BoundedStream_YieldsFastResult_AndCancelsPendingWorkOnEarlyExit()
    {
        var started = 0;
        var stopped = 0;
        await using (var iterator = BoundedAsync.StreamAsync(Enumerable.Range(0, 10000).ToArray(), 3,
            async (i, token) =>
            {
                Interlocked.Increment(ref started);
                if (i == 1) return i;
                try { await Task.Delay(Timeout.Infinite, token); return i; }
                finally { Interlocked.Increment(ref stopped); }
            }, default).GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal((1, 1), iterator.Current);
            Assert.Equal(3, started);
        }
        Assert.Equal(2, stopped);
        Assert.Equal(3, started);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BoundedBatch_FailureOrCancellationStopsRemainingWork(bool fail)
    {
        using var cancellation = new CancellationTokenSource();
        var trigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var stopped = 0;
        var batch = BoundedAsync.SelectAsync(Enumerable.Range(0, 10000).ToArray(), 2, async (i, token) =>
        {
            Interlocked.Increment(ref started);
            try
            {
                if (i == 0 && fail) { await trigger.Task.WaitAsync(token); throw new InvalidOperationException("failure"); }
                await Task.Delay(Timeout.Infinite, token);
                return i;
            }
            finally { Interlocked.Increment(ref stopped); }
        }, cancellation.Token);
        Assert.Equal(2, started);
        if (fail)
        {
            trigger.SetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => batch.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        else
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        Assert.Equal(2, started);
        Assert.Equal(2, stopped);
    }

    [Fact]
    public async Task PublicReconciliationStream_LimitsLargeInputAndStopsAfterEarlyExit()
    {
        var calls = 0;
        using var client = new HttpClient(new TestHttpMessageHandler(async (request, token) =>
        {
            Interlocked.Increment(ref calls);
            if (request.RequestUri!.Query.Contains("ids=Q1&", StringComparison.Ordinal))
                return TestHttpMessageHandler.Json(TestPayloads.EntityResponse(TestPayloads.Entity("Q1", "One")));
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        }));
        using var reconciler = new WikidataReconciler(client, new()
        {
            MaxConcurrency = 2, WikidataRateLimit = ProviderRateLimitOptions.Unthrottled,
            EntityBatchWindow = TimeSpan.Zero
        });
        var requests = Enumerable.Range(1, 10000).Select(i => new ReconciliationRequest { Query = $"Q{i}" }).ToArray();
        await using (var iterator = reconciler.Reconcile.ReconcileBatchStreamAsync(requests).GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(0, iterator.Current.Index);
            Assert.Equal("Q1", Assert.Single(iterator.Current.Results).Id);
        }
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task BoundedBatch_EmptyAndPreCancelledInputsDoNotStartWork()
    {
        var calls = 0;
        Task<int> Work(int item, CancellationToken token) { calls++; return Task.FromResult(item); }
        Assert.Empty(await BoundedAsync.SelectAsync(Array.Empty<int>(), 2, Work, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedAsync.SelectAsync(new[] { 1, 2 }, 2, Work, cancelled.Token));
        Assert.Equal(0, calls);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public void Advance(TimeSpan delay) => Interlocked.Add(ref _ticks, delay.Ticks);
    }
}
