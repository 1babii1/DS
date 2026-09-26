using CSharpFunctionalExtensions;
using DirectoryService.Application.Department;
using DirectoryService.Application.Department.Commands;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations;
using DirectoryService.Domain.Locations.ValueObjects;
using DirectoryService.Infrastructure.Postgres;
using DirectoryService.Infrastructure.Postgres.Backgrounds;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared;

namespace DirectoryService.IntegrationTests;

/// <summary>
/// Written to check a hypothesis from this session's audit, not to demonstrate a known
/// bug: the purge job's raw SQL computes where to cut a survivor's ltree path using
/// nlevel(identifier::ltree) - but identifier is always a single label (department
/// identifiers never contain dots), so that expression is always 1 regardless of which
/// department was actually deleted or how deep it sits. The purge job is only exercised
/// through a manually constructed hierarchy here (its BackgroundService is removed from
/// DI in tests - see DirectoryTestWEbFactory), calling the same ClearDb the scheduled
/// job calls, made internal specifically so this can be verified rather than guessed at.
/// </summary>
public class ClearDbOfDeletedEntitiesTests : IClassFixture<DirectoryTestWEbFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;

    private IServiceProvider Services { get; }

    public ClearDbOfDeletedEntitiesTests(DirectoryTestWEbFactory factory)
    {
        Services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task Purging_a_deleted_department_promotes_surviving_descendants_correctly()
    {
        // Arrange - root -> mid -> leaf. mid is the one that gets soft-deleted and
        // purged; leaf is an untouched survivor two levels below root, not one -
        // the case the buggy fixed "cut at index 1" formula gets wrong.
        var locationId = await CreateLocation();
        var root = await CreateDepartment("root", null, locationId);
        var mid = await CreateDepartment("mid", root, locationId);
        var leaf = await CreateDepartment("leaf", mid, locationId);

        await SoftDeleteAndBackdate(mid, TimeSpan.FromDays(31));

        // Act - the same ClearDb the scheduled BackgroundService calls, with a
        // retention short enough that the backdated delete above is past it.
        var cleared = await RunClearDb(retention: TimeSpan.FromDays(30));
        Assert.True(cleared.IsSuccess);

        // Assert - mid is gone, root is untouched, and leaf was promoted to be root's
        // direct child: path "root.leaf" with depth 1, not left referencing the
        // deleted "mid" label or keeping its old depth of 2.
        await ExecuteInDb(async dbContext =>
        {
            var midStillThere = await dbContext.Departments.AsNoTracking()
                .AnyAsync(d => d.Id == mid);
            Assert.False(midStillThere, "The purged department should have been deleted");

            var leafRow = await dbContext.Departments.AsNoTracking()
                .SingleAsync(d => d.Id == leaf);
            Assert.Equal("root.leaf", leafRow.Path.Value);
            Assert.Equal(1, leafRow.Depth);
            Assert.Equal(root, leafRow.ParentId);

            var rootRow = await dbContext.Departments.AsNoTracking()
                .SingleAsync(d => d.Id == root);
            Assert.Equal("root", rootRow.Path.Value);
        });
    }

    [Fact]
    public async Task Purging_two_ancestors_in_the_same_chain_at_once_still_promotes_correctly()
    {
        // Arrange - root -> a -> b -> leaf. Both a and b (adjacent levels) are purged
        // in the same run - the case a single non-repeating UPDATE pass gets wrong,
        // since removing a's label alone would still leave leaf's path referencing b,
        // which is also about to be deleted in the very same statement.
        var locationId = await CreateLocation();
        var root = await CreateDepartment("root2", null, locationId);
        var a = await CreateDepartment("depa", root, locationId);
        var b = await CreateDepartment("depb", a, locationId);
        var leaf = await CreateDepartment("leaf2", b, locationId);

        await SoftDeleteAndBackdate(a, TimeSpan.FromDays(31));
        await SoftDeleteAndBackdate(b, TimeSpan.FromDays(31));

        var cleared = await RunClearDb(retention: TimeSpan.FromDays(30));
        Assert.True(cleared.IsSuccess);

        await ExecuteInDb(async dbContext =>
        {
            Assert.False(await dbContext.Departments.AsNoTracking().AnyAsync(d => d.Id == a));
            Assert.False(await dbContext.Departments.AsNoTracking().AnyAsync(d => d.Id == b));

            var leafRow = await dbContext.Departments.AsNoTracking().SingleAsync(d => d.Id == leaf);
            Assert.Equal("root2.leaf2", leafRow.Path.Value);
            Assert.Equal(1, leafRow.Depth);
            Assert.Equal(root, leafRow.ParentId);
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private async Task<LocationId> CreateLocation()
    {
        return await ExecuteInDb(async dbContext =>
        {
            var location = new Location(
                LocationId.NewLocationId(),
                LocationName.Create("purge-test-location").Value,
                Timezone.Create("europe/asia").Value,
                Address.Create("street", "city", "country").Value,
                []);
            dbContext.Locations.Add(location);
            await dbContext.SaveChangesAsync();
            return location.Id;
        });
    }

    private async Task<DepartmentId> CreateDepartment(string identifier, DepartmentId? parentId, LocationId locationId)
    {
        await using var scope = Services.CreateAsyncScope();
        var sut = scope.ServiceProvider.GetRequiredService<CreateDepartmentHandler>();

        var result = await sut.Handle(
            new CreateDepartmentCommand(new CreateDepartmentRequest(
                DepartmentName.Create(identifier).Value,
                DepartmentIdentifier.Create(identifier).Value,
                parentId,
                null,
                [locationId],
                DepartmentId.NewDepartmentId())),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        return DepartmentId.FromValue(result.Value);
    }

    private async Task SoftDeleteAndBackdate(DepartmentId departmentId, TimeSpan age)
    {
        await using var scope = Services.CreateAsyncScope();
        var sut = scope.ServiceProvider.GetRequiredService<SoftDeleteDepartmentHandler>();

        var result = await sut.Handle(
            new SoftDeleteDepartmentRequest(departmentId.Value), CancellationToken.None);
        Assert.True(result.IsSuccess);

        // The handler stamps DeletedAt as UtcNow - backdate it directly so it falls
        // outside the retention window without waiting real days for it to age out.
        await ExecuteInDb(async dbContext =>
        {
            var affected = await dbContext.Departments
                .Where(d => d.Id == departmentId)
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(d => d.DeletedAt, DateTime.UtcNow - age));
            Assert.Equal(1, affected);
        });
    }

    private async Task<Result<int, Error>> RunClearDb(TimeSpan retention)
    {
        await using var scope = Services.CreateAsyncScope();
        var worker = new ClearDbOfDeletedEntities(
            scope.ServiceProvider,
            NullLogger<ClearDbOfDeletedEntities>.Instance,
            Options.Create(new ClearDbOptions { Retention = retention }));

        return await worker.ClearDb(CancellationToken.None);
    }

    private async Task<T> ExecuteInDb<T>(Func<DirectoryServiceDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var sut = scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>();
        return await action(sut);
    }

    private async Task ExecuteInDb(Func<DirectoryServiceDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var sut = scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>();
        await action(sut);
    }
}
