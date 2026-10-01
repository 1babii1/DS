using System.Net;
using System.Net.Http.Json;
using DirectoryService.Application.Department;
using DirectoryService.Application.Department.Commands;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations;
using DirectoryService.Domain.Locations.ValueObjects;
using DirectoryService.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Shared;

namespace DirectoryService.IntegrationTests;

/// <summary>
/// Written to prove a real bug, not a hypothesis: DepartmentController's query
/// endpoints call handlers that return a bare nullable/list value, so a FluentValidation
/// failure inside the handler (invalid departmentId, empty search query, page &lt;= 0,
/// ...) collapses to the same C# null/empty result an ASP.NET Core action returns for
/// a legitimate empty result - which ActionResult&lt;T&gt; serializes as 200 OK with no
/// body, not 400. A caller cannot tell "you sent something invalid" apart from
/// "here is your empty/not-found answer". Command endpoints on the same controller
/// already answer errors through the shared Envelope format via EndpointResult&lt;T&gt;;
/// these tests hold the four affected query endpoints (GetDepartmentById, search,
/// children, roots) to that same contract, while a dedicated test keeps the genuine
/// not-found case - a real gap in the org tree, not a bad request - on its existing
/// 200 semantics so the fix doesn't quietly turn a legitimate empty answer into an error.
/// </summary>
public class DepartmentQueryContractTests : IClassFixture<DirectoryTestWEbFactory>, IAsyncLifetime
{
    private readonly DirectoryTestWEbFactory _factory;
    private readonly Func<Task> _resetDatabase;

    private IServiceProvider Services => _factory.Services;

    public DepartmentQueryContractTests(DirectoryTestWEbFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task GetDepartmentById_with_an_empty_guid_returns_400_with_envelope()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/departments/department/{Guid.Empty}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.NotNull(envelope);
        Assert.True(envelope!.IsError);
        Assert.Equal(ErrorType.VALIDATION, envelope.Error!.Type);
    }

    [Fact]
    public async Task GetDepartmentById_for_a_department_that_does_not_exist_still_returns_200_empty()
    {
        // Same request shape as the validation case above - a syntactically valid,
        // non-empty guid - but for a department nobody ever created. This is the
        // case the fix must not disturb: a real gap in the org tree is not a bad
        // request, and stays on the 200 semantics the endpoint already had.
        using var client = _factory.CreateClient();
        var unknownId = Guid.NewGuid();

        var response = await client.GetAsync($"/api/departments/department/{unknownId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.IsError);
        Assert.Null(envelope.Result);
    }

    [Fact]
    public async Task GetChildrenLazy_with_a_zero_page_returns_400_with_envelope()
    {
        using var client = _factory.CreateClient();
        var parentId = await CreateRootDepartment("childrencontract");

        var response = await client.GetAsync($"/api/departments/{parentId}/children?page=0&pageSize=20");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.NotNull(envelope);
        Assert.True(envelope!.IsError);
        Assert.Equal(ErrorType.VALIDATION, envelope.Error!.Type);
    }

    [Fact]
    public async Task GetChildrenLazy_for_a_department_with_no_children_still_returns_200_empty_list()
    {
        // Mirrors the not-found case above: a legitimate empty answer (this
        // department just has no children) must not become an error.
        using var client = _factory.CreateClient();
        var parentId = await CreateRootDepartment("childrenempty");

        var response = await client.GetAsync($"/api/departments/{parentId}/children?page=1&pageSize=20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.NotNull(envelope);
        Assert.False(envelope!.IsError);
    }

    [Fact]
    public async Task GetRootDepartments_with_a_zero_size_returns_400_with_envelope()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/departments/roots?page=1&size=0&preferch=5");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.NotNull(envelope);
        Assert.True(envelope!.IsError);
        Assert.Equal(ErrorType.VALIDATION, envelope.Error!.Type);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private async Task<Guid> CreateRootDepartment(string identifier)
    {
        var locationId = await ExecuteInDb(async dbContext =>
        {
            var location = new Location(
                LocationId.NewLocationId(),
                LocationName.Create($"location-{identifier}").Value,
                Timezone.Create("europe/asia").Value,
                Address.Create("street", "city", "country").Value,
                []);
            dbContext.Locations.Add(location);
            await dbContext.SaveChangesAsync();
            return location.Id;
        });

        await using var scope = Services.CreateAsyncScope();
        var sut = scope.ServiceProvider.GetRequiredService<CreateDepartmentHandler>();

        var result = await sut.Handle(
            new CreateDepartmentCommand(new CreateDepartmentRequest(
                DepartmentName.Create(identifier).Value,
                DepartmentIdentifier.Create(identifier).Value,
                null,
                null,
                [locationId],
                DepartmentId.NewDepartmentId())),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task<T> ExecuteInDb<T>(Func<DirectoryServiceDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var sut = scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>();
        return await action(sut);
    }
}
