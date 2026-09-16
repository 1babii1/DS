using System.Reflection;
using NetArchTest.Rules;

namespace NotificationService.ArchitectureTests;

public class LayerBoundaryTests
{
    private static readonly Assembly DomainAssembly = typeof(NotificationService.Domain.Notification).Assembly;

    private static readonly Assembly InfrastructureAssembly =
        typeof(NotificationService.Infrastructure.Postgres.NotificationDbContext).Assembly;

    private static readonly Assembly WebAssembly =
        typeof(NotificationService.Web.Controllers.NotificationsController).Assembly;

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
    public void Infrastructure_has_no_dependency_on_the_web_layer()
    {
        var result = Types.InAssembly(InfrastructureAssembly)
            .Should()
            .NotHaveDependencyOn(WebAssembly.GetName().Name)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : "Violating types: " + string.Join(", ", result.FailingTypeNames ?? []);
}
