using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Tuvima.Wikidata;

/// <summary>
/// OpenTelemetry-compatible tracing and metrics names. Built on <see cref="ActivitySource"/> and
/// <see cref="Meter"/> from the base class library, so no telemetry packages are required.
/// <para>
/// With OpenTelemetry: <c>tracing.AddSource(WikidataTelemetry.ActivitySourceName)</c> and
/// <c>metrics.AddMeter(WikidataTelemetry.MeterName)</c>.
/// </para>
/// </summary>
public static class WikidataTelemetry
{
    /// <summary>Name of the <see cref="ActivitySource"/> that emits one client span per provider request.</summary>
    public const string ActivitySourceName = "Tuvima.Wikidata";

    /// <summary>Name of the <see cref="Meter"/> that emits provider request metrics.</summary>
    public const string MeterName = "Tuvima.Wikidata";

    private static readonly string Version = WikidataLibraryInfo.PackageVersion.Split('+')[0];

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

    private static readonly Meter Meter = new(MeterName, Version);

    /// <summary>Provider HTTP attempts and cache hits, tagged by host, endpoint, cache and outcome.</summary>
    internal static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "tuvima.wikidata.requests", unit: "{request}",
        description: "Provider HTTP attempts and cache hits.");

    /// <summary>Duration of provider HTTP attempts (cache hits are excluded).</summary>
    internal static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "tuvima.wikidata.request.duration", unit: "s",
        description: "Duration of provider HTTP attempts.");
}
