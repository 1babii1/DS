using System.Reflection;
using NetArchTest.Rules;

namespace DirectoryService.ArchitectureTests;

/// <summary>
/// Makes the layer boundaries this codebase already follows into a convention a build
/// can fail on, not just something a reviewer happens to notice. Each assertion is
/// paired with a concrete failure mode it exists to catch - if that scenario can no
/// longer happen even without the rule, the rule has stopped earning its keep.
/// </summary>
public class LayerBoundaryTests
{
    private static readonly Assembly DomainAssembly =
        typeof(DirectoryService.Domain.Departments.Departments).Assembly;

    private static readonly Assembly ApplicationAssembly =
        typeof(DirectoryService.Application.Department.Commands.CreateDepartmentHandler).Assembly;

    private static readonly Assembly InfrastructureAssembly =
        typeof(DirectoryService.Infrastructure.Postgres.DirectoryServiceDbContext).Assembly;

    private static readonly Assembly WebAssembly =
        typeof(DirectoryService.Controllers.DepartmentController).Assembly;

    [Fact]
    public void Domain_has_no_dependency_on_infrastructure()
    {
        // A Domain type that reaches into Infrastructure.Postgres is the first sign
        // persistence concerns (EF Core attributes, connection handling) have leaked
        // into business rules that should be testable without a database at all.
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOn(InfrastructureAssembly.GetName().Name)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Domain_has_no_dependency_on_the_web_layer()
    {
        // Checking against WebAssembly's simple name ("DirectoryService") would false-
        // positive on every Domain type: this whole solution's assemblies all share
        // that "DirectoryService" root namespace prefix (DirectoryService.Domain,
        // DirectoryService.Application, ...), and NetArchTest matches dependencies by
        // namespace prefix, not by physical assembly identity. The Web layer's own
        // Controllers namespace is what's actually distinctive to it.
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOn("DirectoryService.Controllers")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Domain_has_no_dependency_on_entity_framework_core()
    {
        // Domain entities are plain C# with private setters and factory methods
        // guarding invariants - a direct EF Core reference here would mean either a
        // [Column]-style attribute creeping in, or a type shaped around what EF Core
        // can map rather than what the business rule actually requires.
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Application_has_no_dependency_on_infrastructure()
    {
        // Application defines its own abstractions (IDepartmentRepository,
        // ITransactionManager, IDbConnectionFactory, all under Application/Database) and
        // Infrastructure.Postgres implements them - never the other way around. A
        // handler that new()s up a DbContext or a Dapper connection directly instead of
        // going through one of those interfaces would trip this.
        var result = Types.InAssembly(ApplicationAssembly)
            .Should()
            .NotHaveDependencyOn(InfrastructureAssembly.GetName().Name)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Controllers_have_no_direct_dependency_on_the_dbcontext()
    {
        // Controllers call into Application handlers; going around that to query
        // DirectoryServiceDbContext (or IReadDbContext) directly from a Controller is
        // exactly the "business/data-access logic in the wrong layer" smell this
        // catches, independent of whether Infrastructure is referenced for DI wiring
        // in Program.cs (a controller-level check, not an assembly-level one).
        var result = Types.InAssembly(WebAssembly)
            .That()
            .ResideInNamespace("DirectoryService.Controllers")
            .Should()
            .NotHaveDependencyOnAny(
                "DirectoryService.Infrastructure.Postgres.DirectoryServiceDbContext",
                "DirectoryService.Application.Database.IReadDbContext")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : "Violating types: " + string.Join(", ", result.FailingTypeNames ?? []);
}
