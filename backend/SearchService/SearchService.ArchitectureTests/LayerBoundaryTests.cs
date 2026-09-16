using System.Reflection;
using NetArchTest.Rules;

namespace SearchService.ArchitectureTests;

public class LayerBoundaryTests
{
    private static readonly Assembly DomainAssembly = typeof(SearchService.Domain.SearchDocument).Assembly;

    private static readonly Assembly PostgresAssembly =
        typeof(SearchService.Infrastructure.Postgres.SearchDbContext).Assembly;

    private static readonly Assembly ElasticsearchAssembly =
        typeof(SearchService.Infrastructure.Elasticsearch.SearchIndexClient).Assembly;

    private static readonly Assembly WebAssembly =
        typeof(SearchService.Web.Controllers.SearchController).Assembly;

    [Fact]
    public void Domain_has_no_dependency_on_postgres_infrastructure()
    {
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOn(PostgresAssembly.GetName().Name)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Domain_has_no_dependency_on_elasticsearch_infrastructure()
    {
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOn(ElasticsearchAssembly.GetName().Name)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Domain_has_no_dependency_on_the_web_layer()
    {
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOn(WebAssembly.GetName().Name)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Domain_has_no_dependency_on_entity_framework_core()
    {
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Domain_has_no_dependency_on_elasticsearch_client()
    {
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOn("Elastic.Clients.Elasticsearch")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : "Violating types: " + string.Join(", ", result.FailingTypeNames ?? []);
}
