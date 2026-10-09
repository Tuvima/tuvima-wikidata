using Microsoft.Extensions.DependencyInjection;
using Tuvima.Wikidata.AspNetCore;
using Tuvima.Wikidata.Services;

namespace Tuvima.Wikidata.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddWikidataReconciliation_WithOptionsInstance_UsesThoseOptions()
    {
        var options = new WikidataReconcilerOptions { Language = "de", UserAgent = "DiTest/1.0 (https://example.org)" };
        using var provider = new ServiceCollection()
            .AddWikidataReconciliation(options)
            .BuildServiceProvider();

        var reconciler = provider.GetRequiredService<WikidataReconciler>();
        Assert.NotNull(reconciler);
        Assert.Same(reconciler.Persons, provider.GetRequiredService<PersonsService>());
        Assert.Same(reconciler.Bridge, provider.GetRequiredService<BridgeResolutionService>());

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ServiceCollectionExtensions.HttpClientName);
        Assert.Contains("DiTest/1.0", client.DefaultRequestHeaders.UserAgent.ToString());
        Assert.Equal(options.Timeout, client.Timeout);
    }

    [Fact]
    public void AddWikidataReconciliation_WithFactory_RunsFactoryOnce()
    {
        var calls = 0;
        using var provider = new ServiceCollection()
            .AddWikidataReconciliation(_ =>
            {
                calls++;
                return new WikidataReconcilerOptions { UserAgent = "Factory/2.0 (https://example.org)" };
            })
            .BuildServiceProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ServiceCollectionExtensions.HttpClientName);
        Assert.NotNull(provider.GetRequiredService<WikidataReconciler>());
        Assert.Contains("Factory/2.0", client.DefaultRequestHeaders.UserAgent.ToString());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AddWikidataReconciliation_WithExpressionFactory_Compiles()
    {
        using var provider = new ServiceCollection()
            .AddWikidataReconciliation(sp => new WikidataReconcilerOptions { Language = "fr" })
            .BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<WikidataReconciler>());
    }

    [Fact]
    public void AddWikidataReconciliation_Parameterless_UsesDefaultUserAgent()
    {
        using var provider = new ServiceCollection()
            .AddWikidataReconciliation()
            .BuildServiceProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ServiceCollectionExtensions.HttpClientName);
        Assert.Contains("Tuvima.Wikidata/", client.DefaultRequestHeaders.UserAgent.ToString());
        Assert.DoesNotContain("Tuvima.Wikidata/1.0 ", WikidataReconcilerOptions.DefaultUserAgent);
    }
}
