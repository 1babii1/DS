using System.Reflection;
using NetArchTest.Rules;

namespace AuthService.ArchitectureTests;

/// <summary>
/// AuthService is intentionally thinner than DirectoryService (no separate Application
/// handler layer - ASP.NET Identity's UserManager/SignInManager plus OpenIddict carry
/// most of the logic directly from Controllers), so this covers what actually applies
/// to its shape: Domain stays free of persistence and web concerns, and Controllers
/// go through Identity/OpenIddict abstractions rather than AuthDbContext directly.
/// </summary>
public class LayerBoundaryTests
{
    private static readonly Assembly DomainAssembly = typeof(AuthService.Domain.Account).Assembly;

    private static readonly Assembly InfrastructureAssembly =
        typeof(AuthService.Infrastructure.Postgres.AuthDbContext).Assembly;

    private static readonly Assembly WebAssembly =
        typeof(AuthService.Web.Controllers.AccountController).Assembly;

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
        // Account/Role only depend on Microsoft.AspNetCore.Identity (IdentityUser<Guid>,
        // IdentityRole<Guid>) - Identity's own abstractions, not EF Core directly. A
        // direct EF Core reference here would mean a persistence detail (a [Column]
        // attribute, a navigation property shaped for a specific mapping) leaking in.
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Controllers_have_no_direct_dependency_on_the_dbcontext()
    {
        // AccountController/AuthorizationController work through UserManager,
        // SignInManager, and IOpenIddictApplicationManager - never AuthDbContext
        // itself. Infrastructure is still referenced by the Web project for DI wiring
        // in Program.cs; this checks the controller types specifically, not the
        // project-level reference.
        var result = Types.InAssembly(WebAssembly)
            .That()
            .ResideInNamespace("AuthService.Web.Controllers")
            .Should()
            .NotHaveDependencyOn("AuthService.Infrastructure.Postgres.AuthDbContext")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : "Violating types: " + string.Join(", ", result.FailingTypeNames ?? []);
}
