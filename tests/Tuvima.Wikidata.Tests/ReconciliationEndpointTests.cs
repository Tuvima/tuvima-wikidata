using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tuvima.Wikidata.AspNetCore;

namespace Tuvima.Wikidata.Tests;

public class ReconciliationEndpointTests
{
    [Theory]
    [InlineData("query", "{bad")]
    [InlineData("query", "null")]
    [InlineData("query", "[]")]
    [InlineData("query", "{}")]
    [InlineData("query", "{\"query\":\"   \"}")]
    [InlineData("query", "{\"query\":123}")]
    [InlineData("query", "{\"query\":\"Q1\",\"limit\":\"five\"}")]
    [InlineData("query", "{\"query\":\"Q1\",\"limit\":-1}")]
    [InlineData("query", "{\"query\":\"Q1\",\"properties\":[null]}")]
    [InlineData("query", "{\"query\":\"Q1\",\"properties\":[{}]}")]
    [InlineData("queries", "{\"ok\":{\"query\":\"Q1\"},\"bad\":null}")]
    [InlineData("queries", "{\"ok\":{\"query\":\"Q1\"},\"bad\":{\"query\":\" \"}}")]
    [InlineData("queries", "null")]
    [InlineData("queries", "{}")]
    [InlineData("queries", "[]")]
    public async Task InvalidPayload_Returns400WithoutProviderRequests(string field, string json)
    {
        await using var host = await EndpointHost.StartAsync();
        using var response = await host.Client.PostAsync("/reconcile", Form(field, json));
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Empty(host.Provider.RequestedUris);
    }

    [Theory]
    [InlineData("query", "{\"query\":\"too long\"}")]
    [InlineData("query", "{\"query\":\"Q1\",\"limit\":3}")]
    [InlineData("query", "{\"query\":\"Q1\",\"properties\":[{\"pid\":\"P1\",\"v\":\"x\"},{\"pid\":\"P2\",\"v\":\"y\"}]}")]
    [InlineData("queries", "{\"a\":{\"query\":\"Q1\"},\"b\":{\"query\":\"Q2\"}}")]
    public async Task ConfiguredLimits_AreCheckedBeforeAnyProviderWork(string field, string json)
    {
        await using var host = await EndpointHost.StartAsync(options =>
        {
            options.MaxQueryLength = 4;
            options.MaxBatchSize = 1;
            options.MaxResultLimit = 2;
            options.MaxPropertiesPerQuery = 1;
        });
        using var response = await host.Client.PostAsync("/reconcile", Form(field, json));
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Empty(host.Provider.RequestedUris);
    }

    [Fact]
    public async Task MissingDuplicateOrAmbiguousParameters_Return400()
    {
        await using var host = await EndpointHost.StartAsync();
        foreach (var pairs in new[]
        {
            Array.Empty<KeyValuePair<string, string>>(),
            new[] { KeyValuePair.Create("query", "{}"), KeyValuePair.Create("queries", "{}") },
            new[] { KeyValuePair.Create("query", "{}"), KeyValuePair.Create("query", "{}") }
        })
        {
            using var response = await host.Client.PostAsync("/reconcile", new FormUrlEncodedContent(pairs));
            await AssertProblem(response, HttpStatusCode.BadRequest);
        }
        Assert.Empty(host.Provider.RequestedUris);
    }

    [Fact]
    public async Task UnsupportedContentType_Returns415()
    {
        await using var host = await EndpointHost.StartAsync();
        using var response = await host.Client.PostAsync("/reconcile", new StringContent("{}", Encoding.UTF8, "application/json"));
        await AssertProblem(response, HttpStatusCode.UnsupportedMediaType);
        Assert.Empty(host.Provider.RequestedUris);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyLimit_AppliesWithAndWithoutContentLength(bool unknownLength)
    {
        await using var host = await EndpointHost.StartAsync(options => options.MaxRequestBodyBytes = 32);
        using HttpContent content = unknownLength
            ? new UnknownLengthForm(new string('x', 100))
            : new StringContent(new string('x', 100), Encoding.UTF8, "application/x-www-form-urlencoded");
        using var response = await host.Client.PostAsync("/reconcile", content);
        await AssertProblem(response, HttpStatusCode.RequestEntityTooLarge);
        Assert.Empty(host.Provider.RequestedUris);
    }

    [Fact]
    public async Task MalformedMultipart_Returns400()
    {
        await using var host = await EndpointHost.StartAsync();
        using var content = new StringContent("broken form");
        content.Headers.ContentType = new("multipart/form-data");
        using var response = await host.Client.PostAsync("/reconcile", content);
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Empty(host.Provider.RequestedUris);
    }

    [Fact]
    public async Task ValidSingleQuery_PreservesResponseShapeAndLanguage()
    {
        await using var host = await EndpointHost.StartAsync();
        host.Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr");
        using var response = await host.Client.PostAsync("/reconcile", Form("query", "{\"query\":\"Q1\"}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Q1", json.RootElement.GetProperty("result")[0].GetProperty("id").GetString());
        Assert.Equal("Exemple", json.RootElement.GetProperty("result")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ValidBatch_PreservesCorrelationKeys()
    {
        await using var host = await EndpointHost.StartAsync();
        using var response = await host.Client.PostAsync("/reconcile", Form("queries", "{\"second\":{\"query\":\"Q2\"},\"first\":{\"query\":\"Q1\"}}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Q2", json.RootElement.GetProperty("second")[0].GetProperty("id").GetString());
        Assert.Equal("Q1", json.RootElement.GetProperty("first")[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("property")]
    [InlineData("type")]
    public async Task Suggest_RejectsOversizedPrefix_ButPreservesEmptyResponse(string kind)
    {
        await using var host = await EndpointHost.StartAsync(options => options.MaxQueryLength = 3);
        using var oversized = await host.Client.GetAsync($"/reconcile/suggest/{kind}?prefix=long");
        await AssertProblem(oversized, HttpStatusCode.BadRequest);
        using var empty = await host.Client.GetAsync($"/reconcile/suggest/{kind}");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Empty(host.Provider.RequestedUris);
    }

    [Fact]
    public async Task ValidMultipartQuery_AtConfiguredTextAndBatchLimits_Succeeds()
    {
        await using var host = await EndpointHost.StartAsync(options =>
        {
            options.MaxBatchSize = 1;
            options.MaxQueryLength = 2;
            options.MaxResultLimit = 1;
        });
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("{\"query\":\"Q1\",\"limit\":1}"), "query");
        using var response = await host.Client.PostAsync("/reconcile", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task InvalidLimitConfiguration_FailsAtEndpointMapping(int setting)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => EndpointHost.StartAsync(options =>
        {
            switch (setting)
            {
                case 0: options.MaxBatchSize = 0; break;
                case 1: options.MaxQueryLength = 0; break;
                case 2: options.MaxResultLimit = 0; break;
                case 3: options.MaxPropertiesPerQuery = 0; break;
                case 4: options.MaxRequestBodyBytes = 0; break;
            }
        }));
    }

    [Fact]
    public async Task ClientCancellation_CancelsProviderWork()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await EndpointHost.StartAsync(handler: async (_, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped.SetResult(); }
            return TestHttpMessageHandler.Json("{}");
        });
        using var cancellation = new CancellationTokenSource();
        var response = host.Client.PostAsync("/reconcile", Form("query", "{\"query\":\"Q1\"}"), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response.WaitAsync(TimeSpan.FromSeconds(5)));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Manifest_AdvertisesVersionsAndDataExtension()
    {
        await using var host = await EndpointHost.StartAsync();
        using var json = JsonDocument.Parse(await host.Client.GetStringAsync("/reconcile"));
        var root = json.RootElement;

        Assert.Equal(["0.1", "0.2"], root.GetProperty("versions").EnumerateArray().Select(v => v.GetString()));
        var propose = root.GetProperty("extend").GetProperty("propose_properties");
        Assert.EndsWith("/reconcile", propose.GetProperty("service_url").GetString());
        Assert.Equal("/propose_properties", propose.GetProperty("service_path").GetString());
        Assert.Equal("limit", root.GetProperty("extend").GetProperty("property_settings")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Extend_ReturnsBestRankValuesWithLabelsAndTypedCells()
    {
        await using var host = await EndpointHost.StartAsync(handler: (request, _) =>
        {
            var ids = IdsParam(request);
            object payload = ids switch
            {
                "Q1|Q404" => TestPayloads.EntityResponse(TestPayloads.Entity("Q1", "Good Omens", claims: TestPayloads.Claims(
                    ("P50", "wikibase-item", TestPayloads.ItemDataValue("Q210059"), "normal"),
                    ("P50", "wikibase-item", TestPayloads.ItemDataValue("Q46248"), "normal"),
                    ("P50", "wikibase-item", TestPayloads.ItemDataValue("Q999"), "deprecated"),
                    ("P577", "time", TestPayloads.TimeDataValue("+1990-05-00T00:00:00Z", precision: 10), "normal"),
                    ("P1104", "quantity", new { value = new { amount = "+288", unit = "1" }, type = "quantity" }, "normal")))),
                "Q210059|Q46248|Q999" => TestPayloads.EntityResponse(
                    TestPayloads.Entity("Q210059", "Neil Gaiman"), TestPayloads.Entity("Q46248", "Terry Pratchett")),
                "P1104|P50|P577" => TestPayloads.EntityResponse(
                    TestPayloads.Entity("P50", "author"), TestPayloads.Entity("P577", "publication date"),
                    TestPayloads.Entity("P1104", "number of pages")),
                _ => throw new InvalidOperationException($"Unexpected ids: {ids}")
            };
            return Task.FromResult(TestHttpMessageHandler.Json((string)payload));
        });

        using var response = await host.Client.PostAsync("/reconcile", Form("extend",
            """{"ids":["Q1","Q404"],"properties":[{"id":"P50","settings":{"limit":"1"}},{"id":"P50"},{"id":"P577"},{"id":"P1104"}]}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal("author", root.GetProperty("meta")[0].GetProperty("name").GetString());
        var row = root.GetProperty("rows").GetProperty("Q1");
        var authors = row.GetProperty("P50");
        Assert.Equal(2, authors.GetArrayLength());
        Assert.Equal("Neil Gaiman", authors[0].GetProperty("name").GetString());
        Assert.Equal("Q46248", authors[1].GetProperty("id").GetString());
        Assert.Equal("1990-05-01T00:00:00+00:00", row.GetProperty("P577")[0].GetProperty("date").GetString());
        Assert.Equal(288, row.GetProperty("P1104")[0].GetProperty("float").GetDouble());
        Assert.Equal(0, root.GetProperty("rows").GetProperty("Q404").GetProperty("P50").GetArrayLength());
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("{}")]
    [InlineData("""{"ids":[],"properties":[{"id":"P31"}]}""")]
    [InlineData("""{"ids":["Q1"],"properties":[]}""")]
    [InlineData("""{"ids":["Q1"],"properties":[{"id":"Q5"}]}""")]
    [InlineData("""{"ids":["P31"],"properties":[{"id":"P31"}]}""")]
    [InlineData("""{"ids":["Q1"],"properties":[null]}""")]
    public async Task Extend_InvalidPayload_Returns400WithoutProviderRequests(string json)
    {
        await using var host = await EndpointHost.StartAsync();
        using var response = await host.Client.PostAsync("/reconcile", Form("extend", json));
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Empty(host.Provider.RequestedUris);
    }

    [Fact]
    public async Task ProposeProperties_UsesPropertiesForThisType()
    {
        await using var host = await EndpointHost.StartAsync(handler: (request, _) =>
        {
            var ids = IdsParam(request);
            var payload = ids switch
            {
                "Q5" => TestPayloads.EntityResponse(TestPayloads.Entity("Q5", "human", claims: TestPayloads.Claims(
                    ("P1963", "wikibase-property", PropertyDataValue("P569"), "normal"),
                    ("P1963", "wikibase-property", PropertyDataValue("P19"), "normal"),
                    ("P1963", "wikibase-property", PropertyDataValue("P27"), "normal")))),
                "P19|P27|P569" => TestPayloads.EntityResponse(
                    TestPayloads.Entity("P569", "date of birth"), TestPayloads.Entity("P19", "place of birth"),
                    TestPayloads.Entity("P27", "country of citizenship")),
                _ => throw new InvalidOperationException($"Unexpected ids: {ids}")
            };
            return Task.FromResult(TestHttpMessageHandler.Json(payload));
        });

        using var json = JsonDocument.Parse(await host.Client.GetStringAsync("/reconcile/propose_properties?type=Q5&limit=2"));
        var root = json.RootElement;

        Assert.Equal("Q5", root.GetProperty("type").GetString());
        var properties = root.GetProperty("properties");
        Assert.Equal(2, properties.GetArrayLength());
        Assert.Equal("P569", properties[0].GetProperty("id").GetString());
        Assert.Equal("date of birth", properties[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("type=human")]
    [InlineData("type=Q5&limit=0")]
    [InlineData("type=Q5&limit=abc")]
    [InlineData("type=Q5&limit=500")]
    public async Task ProposeProperties_InvalidParameters_Return400(string query)
    {
        await using var host = await EndpointHost.StartAsync();
        using var response = await host.Client.GetAsync($"/reconcile/propose_properties?{query}");
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Empty(host.Provider.RequestedUris);
    }

    private static string IdsParam(HttpRequestMessage request)
    {
        var query = Uri.UnescapeDataString(request.RequestUri!.Query);
        var start = query.IndexOf("ids=", StringComparison.Ordinal) + 4;
        var end = query.IndexOf('&', start);
        return end < 0 ? query[start..] : query[start..end];
    }

    private static object PropertyDataValue(string pid)
        => new { value = new { id = pid, entity_type = "property" }, type = "wikibase-entityid" };

    private static FormUrlEncodedContent Form(string field, string json)
        => new([KeyValuePair.Create(field, json)]);

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)expected, json.RootElement.GetProperty("status").GetInt32());
    }

    private sealed class EndpointHost(WebApplication app, HttpClient client, HttpClient providerClient, TestHttpMessageHandler provider) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public TestHttpMessageHandler Provider => provider;
        public static async Task<EndpointHost> StartAsync(Action<ReconciliationServiceOptions>? configure = null,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? handler = null)
        {
            var provider = new TestHttpMessageHandler(handler ?? ((request, _) =>
            {
                var qid = request.RequestUri!.Query.Contains("ids=Q2", StringComparison.Ordinal) ? "Q2" : "Q1";
                var entity = TestPayloads.Entity(qid, "Example");
                entity["labels"] = TestPayloads.Labels(("en", "Example"), ("fr", "Exemple"));
                return Task.FromResult(TestHttpMessageHandler.Json(TestPayloads.EntityResponse(entity)));
            }));
            var providerClient = new HttpClient(provider);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(_ => new WikidataReconciler(providerClient, new()
            {
                WikidataRateLimit = ProviderRateLimitOptions.Unthrottled
            }));
            var app = builder.Build();
            try
            {
                app.MapReconciliation(configure: configure);
                await app.StartAsync();
                return new(app, app.GetTestClient(), providerClient, provider);
            }
            catch
            {
                await app.DisposeAsync();
                providerClient.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            providerClient.Dispose();
        }
    }

    private sealed class UnknownLengthForm : HttpContent
    {
        private readonly string _body;
        public UnknownLengthForm(string body)
        {
            _body = body;
            Headers.ContentType = new("application/x-www-form-urlencoded");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(Encoding.UTF8.GetBytes(_body)).AsTask();
    }
}
