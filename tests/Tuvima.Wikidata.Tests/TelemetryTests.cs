using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Tuvima.Wikidata.Tests;

public class TelemetryTests
{
    [Fact]
    public async Task ProviderRequests_EmitActivitiesAndMetrics()
    {
        var activities = new ConcurrentBag<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == WikidataTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(activityListener);

        // Tag with a unique endpoint marker so parallel tests can't pollute the assertions.
        var marker = "telemetry-" + Guid.NewGuid().ToString("N");
        var measurements = new ConcurrentBag<(string Instrument, string? Outcome)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == WikidataTelemetry.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? outcome = null;
            var matches = false;
            foreach (var tag in tags)
            {
                if (tag.Key == "tuvima.wikidata.outcome") outcome = tag.Value as string;
                if (tag.Key == "server.address" && tag.Value as string == marker + ".example") matches = true;
            }
            if (matches)
                measurements.Add((instrument.Name, outcome));
        }
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Record(instrument, tags));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Record(instrument, tags));
        meterListener.Start();

        var calls = 0;
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(
            Interlocked.Increment(ref calls) == 1
                ? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                : TestHttpMessageHandler.Json(TestPayloads.SearchResponse(("Q42", "Douglas Adams")))));

        using var reconciler = new WikidataReconciler(TestPayloads.CreateHttpClient(handler), new WikidataReconcilerOptions
        {
            ApiEndpoint = $"https://{marker}.example/w/api.php",
            DefaultRateLimit = ProviderRateLimitOptions.Unthrottled,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromMilliseconds(1),
            EnableResponseCaching = false
        });

        await reconciler.Reconcile.SuggestAsync("douglas");

        var activity = Assert.Single(activities, a => a.GetTagItem("server.address") as string == marker + ".example");
        Assert.Equal(ActivityKind.Client, activity.Kind);
        Assert.Equal("wbsearchentities", activity.GetTagItem("tuvima.wikidata.endpoint"));
        Assert.Equal(2, activity.GetTagItem("tuvima.wikidata.attempts"));
        Assert.Contains(activity.Events, e => e.Name == "attempt.failed");

        meterListener.RecordObservableInstruments();
        Assert.Contains(measurements, m => m.Instrument == "tuvima.wikidata.requests" && m.Outcome == "Success");
        Assert.Contains(measurements, m => m.Instrument == "tuvima.wikidata.requests" && m.Outcome == "TransientNetworkFailure");
        Assert.Equal(2, measurements.Count(m => m.Instrument == "tuvima.wikidata.request.duration"));
    }
}
