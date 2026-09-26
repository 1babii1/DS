using System.Reflection;
using NetArchTest.Rules;

namespace RewardsService.ArchitectureTests;

public class LayerBoundaryTests
{
    private static readonly Assembly DomainAssembly = typeof(RewardsService.Domain.Wallet).Assembly;

    private static readonly Assembly InfrastructureAssembly =
        typeof(RewardsService.Infrastructure.RewardsDbContext).Assembly;

    private static readonly Assembly WebAssembly =
        typeof(RewardsService.Web.Controllers.RewardsController).Assembly;

    [Fact]
    public void Domain_has_no_dependency_on_infrastructure()
    {
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOn(InfrastructureAssembly.GetName().Name)
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
    public void Domain_has_no_dependency_on_kafka()
    {
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOn("Confluent.Kafka")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : "Violating types: " + string.Join(", ", result.FailingTypeNames ?? []);
}
