using Microsoft.Extensions.DependencyInjection;
using Tuvima.Wikidata.Services;

namespace Tuvima.Wikidata.AspNetCore;

/// <summary>
/// Extension methods for registering <see cref="WikidataReconciler"/> and its sub-services
/// with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Name of the <see cref="HttpClient"/> registered for the reconciler.</summary>
    public const string HttpClientName = "Tuvima.Wikidata";

    /// <summary>
    /// Registers <see cref="WikidataReconciler"/> as a singleton with default options and a named HttpClient,
    /// along with each sub-service (<see cref="ReconciliationService"/>, <see cref="EntityService"/>,
    /// <see cref="WikipediaService"/>, <see cref="EditionService"/>, <see cref="ChildrenService"/>,
    /// <see cref="AuthorsService"/>, <see cref="LabelsService"/>, <see cref="PersonsService"/>,
    /// <see cref="SeriesManifestService"/>, <see cref="BridgeResolutionService"/>).
    /// <para>
    /// Advanced consumers can inject a specific sub-service directly without going through the facade.
    /// </para>
    /// </summary>
    public static IServiceCollection AddWikidataReconciliation(this IServiceCollection services)
        => services.AddWikidataReconciliation(new WikidataReconcilerOptions());

    /// <summary>
    /// Registers the reconciler and its sub-services using the supplied options.
    /// <paramref name="configureHttpClient"/> can add handlers (proxies, logging) to the named HttpClient;
    /// retries and throttling are already handled by the library, so app-level resilience handlers are not needed.
    /// </summary>
    public static IServiceCollection AddWikidataReconciliation(
        this IServiceCollection services,
        WikidataReconcilerOptions options,
        Action<IHttpClientBuilder>? configureHttpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services.AddWikidataReconciliation(_ => options, configureHttpClient);
    }

    /// <summary>
    /// Registers the reconciler and its sub-services, building options from the service provider
    /// (for example from <c>IConfiguration</c> or other registered services). The factory runs once.
    /// </summary>
    public static IServiceCollection AddWikidataReconciliation(
        this IServiceCollection services,
        Func<IServiceProvider, WikidataReconcilerOptions> optionsFactory,
        Action<IHttpClientBuilder>? configureHttpClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        services.AddSingleton(new WikidataReconcilerOptionsHolder(optionsFactory));

        var httpClient = services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<WikidataReconcilerOptionsHolder>().Get(sp);
            client.Timeout = options.Timeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        });
        configureHttpClient?.Invoke(httpClient);

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<WikidataReconcilerOptionsHolder>().Get(sp);
            var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            return new WikidataReconciler(client, options);
        });

        // Sub-service registrations — each resolves from the facade so all share the same
        // context (HttpClient, provider-safe HTTP pipeline, diagnostics, cache hook, options).
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Reconcile);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Entities);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Wikipedia);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Editions);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Children);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Authors);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Labels);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Persons);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Series);
        services.AddSingleton(sp => sp.GetRequiredService<WikidataReconciler>().Bridge);

        return services;
    }

    /// <summary>
    /// Registers the reconciler with default options.
    /// </summary>
    /// <remarks>
    /// <see cref="WikidataReconcilerOptions"/> properties are init-only, so this callback cannot change them.
    /// Use the overload that takes a <see cref="WikidataReconcilerOptions"/> instance or factory instead.
    /// </remarks>
    [Obsolete("WikidataReconcilerOptions is init-only, so this callback cannot change options. " +
              "Use AddWikidataReconciliation(new WikidataReconcilerOptions { ... }) or the factory overload.")]
    public static IServiceCollection AddWikidataReconciliation(
        this IServiceCollection services,
        Action<WikidataReconcilerOptions>? configure)
    {
        var options = new WikidataReconcilerOptions();
        configure?.Invoke(options);
        return services.AddWikidataReconciliation(options);
    }

    private sealed class WikidataReconcilerOptionsHolder(Func<IServiceProvider, WikidataReconcilerOptions> factory)
    {
        private readonly object _gate = new();
        private WikidataReconcilerOptions? _options;

        public WikidataReconcilerOptions Get(IServiceProvider services)
        {
            lock (_gate)
                return _options ??= factory(services)
                    ?? throw new InvalidOperationException("The WikidataReconcilerOptions factory returned null.");
        }
    }
}
