using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.HealthChecks;

namespace SearchService.Infrastructure.Elasticsearch;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddSearchElasticsearchInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var uri = configuration["Elasticsearch:Uri"]
            ?? throw new InvalidOperationException("Configuration 'Elasticsearch:Uri' is not set.");
        var indexName = configuration["Elasticsearch:IndexName"] ?? "search-entries";

        services.Configure<ElasticsearchOptions>(options =>
        {
            options.Uri = uri;
            options.IndexName = indexName;
        });

        var settings = new ElasticsearchClientSettings(new Uri(uri)).DefaultIndex(indexName);
        services.AddSingleton(new ElasticsearchClient(settings));
        services.AddSingleton<SearchIndexClient>();

        services.AddHealthChecks().AddCheck<ElasticsearchHealthCheck>("elasticsearch", tags: [HealthCheckExtensions.ReadyTag]);

        return services;
    }
}
