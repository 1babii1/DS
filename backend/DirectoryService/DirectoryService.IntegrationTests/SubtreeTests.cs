using DirectoryService.Application.Database;
using DirectoryService.Application.Department;
using DirectoryService.Application.Department.Commands;
using DirectoryService.Application.Department.Queries;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations;
using DirectoryService.Domain.Locations.ValueObjects;
using DirectoryService.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace DirectoryService.IntegrationTests;

// Against a real Postgres with ltree: the subtree is one path query, so what matters is the query's
// own behavior - who is in it, in what order, what is left out and where it is cut off.
public class SubtreeTests : IClassFixture<DirectoryTestWEbFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private IServiceProvider Services { get; }

    public SubtreeTests(DirectoryTestWEbFactory factory)
    {
        Services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    [Fact]
    public async Task The_subtree_of_a_root_department_includes_the_root_and_every_descendant_shallowest_first()
    {
        var tree = await BuildTree();

        var result = await Subtree(tree.Engineering);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Truncated);
        Assert.Equal(
            ["engineering", "billing", "platform", "ledger"],
            result.Value.Nodes.Select(n => n.Name).ToArray());
        Assert.Null(result.Value.Nodes[0].ParentId);
        Assert.Equal(tree.Engineering.Value, result.Value.Nodes[1].ParentId);
        Assert.Equal(tree.Billing.Value, result.Value.Nodes[3].ParentId);
    }

    [Fact]
    public async Task The_subtree_of_a_child_holds_only_that_branch()
    {
        var tree = await BuildTree();

        var result = await Subtree(tree.Billing);

        Assert.Equal(["billing", "ledger"], result.Value.Nodes.Select(n => n.Name).ToArray());
    }

    [Fact]
    public async Task A_deleted_department_and_what_hangs_under_it_are_not_in_the_tree()
    {
        var tree = await BuildTree();
        await using (var scope = Services.CreateAsyncScope())
        {
            var delete = scope.ServiceProvider.GetRequiredService<SoftDeleteDepartmentHandler>();
            Assert.True((await delete.Handle(new SoftDeleteDepartmentRequest(tree.Billing.Value), CancellationToken.None)).IsSuccess);
        }

        var result = await Subtree(tree.Engineering);

        Assert.Equal(["engineering", "platform"], result.Value.Nodes.Select(n => n.Name).ToArray());
    }

    [Fact]
    public async Task An_unknown_department_has_an_empty_tree_not_an_error()
    {
        var result = await Subtree(DepartmentId.NewDepartmentId());

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Nodes);
        Assert.False(result.Value.Truncated);
    }

    [Fact]
    public async Task An_empty_id_is_a_validation_error()
    {
        await using var scope = Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetSubtreeHandler>();

        var result = await handler.Handle(Guid.Empty, CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task A_tree_bigger_than_the_cap_is_cut_and_says_so()
    {
        var tree = await BuildTree();
        await using var scope = Services.CreateAsyncScope();
        var handler = new GetSubtreeHandler(
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>(), maxNodes: 2);

        var result = await handler.Handle(tree.Engineering.Value, CancellationToken.None);

        Assert.Equal(2, result.Value.Nodes.Count);
        Assert.True(result.Value.Truncated);
    }

    [Fact]
    public async Task A_tree_exactly_at_the_cap_is_not_reported_as_truncated()
    {
        var tree = await BuildTree();
        await using var scope = Services.CreateAsyncScope();
        var handler = new GetSubtreeHandler(
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>(), maxNodes: 4);

        var result = await handler.Handle(tree.Engineering.Value, CancellationToken.None);

        Assert.Equal(4, result.Value.Nodes.Count);
        Assert.False(result.Value.Truncated);
    }

    private async Task<CSharpFunctionalExtensions.Result<DirectoryService.Contracts.Response.Department.DepartmentSubtreeDto, Shared.Error>> Subtree(DepartmentId id)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GetSubtreeHandler>().Handle(id.Value, CancellationToken.None);
    }

    // engineering
    //   |- billing
    //   |    `- ledger
    //   `- platform
    private async Task<(DepartmentId Engineering, DepartmentId Billing)> BuildTree()
    {
        var location = await CreateLocation();
        var engineering = await CreateDepartment("engineering", null, location);
        var billing = await CreateDepartment("billing", engineering, location);
        await CreateDepartment("platform", engineering, location);
        await CreateDepartment("ledger", billing, location);
        return (engineering, billing);
    }

    private async Task<LocationId> CreateLocation()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>();
        var location = new Location(
            LocationId.NewLocationId(),
            LocationName.Create("subtree-test-location").Value,
            Timezone.Create("europe/asia").Value,
            Address.Create("street", "city", "country").Value,
            []);
        db.Locations.Add(location);
        await db.SaveChangesAsync();
        return location.Id;
    }

    private async Task<DepartmentId> CreateDepartment(string name, DepartmentId? parent, LocationId location)
    {
        await using var scope = Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<CreateDepartmentHandler>();
        var result = await handler.Handle(
            new CreateDepartmentCommand(new CreateDepartmentRequest(
                DepartmentName.Create(name).Value,
                DepartmentIdentifier.Create(name).Value,
                parent,
                null,
                [location],
                DepartmentId.NewDepartmentId())),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        return DepartmentId.FromValue(result.Value);
    }
}
