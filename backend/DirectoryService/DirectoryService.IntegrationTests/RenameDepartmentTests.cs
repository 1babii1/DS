using CSharpFunctionalExtensions;
using DirectoryService.Application.Department;
using DirectoryService.Application.Department.Commands;
using DirectoryService.Application.Department.Queries;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations;
using DirectoryService.Domain.Locations.ValueObjects;
using DirectoryService.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared;
using Shared.Outbox;

namespace DirectoryService.IntegrationTests;

// A department's name is the one thing every index of it is built from. Renaming it has to be atomic with the
// event that tells the indexes, or an index keeps the old name forever: the event is written in the same
// transaction as the change, and nothing is written when nothing changed.
public class RenameDepartmentTests : IClassFixture<DirectoryTestWEbFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private IServiceProvider Services { get; }

    public RenameDepartmentTests(DirectoryTestWEbFactory factory)
    {
        Services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task A_rename_changes_the_name_only_and_publishes_DepartmentRenamed_in_the_same_transaction()
    {
        var id = await CreateDepartment("Payments", "payments");

        var result = await Rename(id, "Billing and payments");

        Assert.True(result.IsSuccess);
        await InDb(async db =>
        {
            var department = await db.Departments.AsNoTracking().SingleAsync(d => d.Id == DepartmentId.FromValue(id));
            Assert.Equal("Billing and payments", department.Name.Value);
            Assert.Equal("payments", department.Identifier.Value);
            Assert.Equal("payments", department.Path.Value);

            var renamed = await db.Set<OutboxMessage>().AsNoTracking()
                .SingleAsync(m => m.Type == "DepartmentRenamed" && m.AggregateId == id.ToString());
            Assert.Contains("Billing and payments", renamed.Payload);
            Assert.Contains("payments", renamed.Payload);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    public async Task An_invalid_name_is_refused_and_nothing_changes_or_is_published(string name)
    {
        var id = await CreateDepartment("Payments", "payments");

        var result = await Rename(id, name);

        Assert.True(result.IsFailure);
        await InDb(async db =>
        {
            var department = await db.Departments.AsNoTracking().SingleAsync(d => d.Id == DepartmentId.FromValue(id));
            Assert.Equal("Payments", department.Name.Value);
            Assert.False(await db.Set<OutboxMessage>().AnyAsync(m => m.Type == "DepartmentRenamed"));
        });
    }

    [Fact]
    public async Task An_unknown_department_is_not_found_and_nothing_is_published()
    {
        var result = await Rename(Guid.NewGuid(), "Anything at all");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.NOT_FOUND, result.Error.Type);
        await InDb(async db => Assert.False(await db.Set<OutboxMessage>().AnyAsync(m => m.Type == "DepartmentRenamed")));
    }

    [Fact]
    public async Task A_deleted_department_cannot_be_renamed()
    {
        var id = await CreateDepartment("Payments", "payments");
        await InDb(async db =>
        {
            var department = await db.Departments.SingleAsync(d => d.Id == DepartmentId.FromValue(id));
            department.Delete();
            await db.SaveChangesAsync();
        });

        var result = await Rename(id, "Back from the dead");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.NOT_FOUND, result.Error.Type);
        await InDb(async db => Assert.False(await db.Set<OutboxMessage>().AnyAsync(m => m.Type == "DepartmentRenamed")));
    }

    [Fact]
    public async Task Renaming_to_the_name_it_already_has_succeeds_and_publishes_nothing()
    {
        var id = await CreateDepartment("Payments", "payments");

        var result = await Rename(id, " Payments ");

        Assert.True(result.IsSuccess);
        await InDb(async db => Assert.False(await db.Set<OutboxMessage>().AnyAsync(m => m.Type == "DepartmentRenamed")));
    }

    // Departments are cached for minutes; a rename that left the old name in the cache would show it long after
    // the index had moved on.
    [Fact]
    public async Task After_a_rename_reading_the_department_returns_the_new_name_not_a_cached_old_one()
    {
        var id = await CreateDepartment("Payments", "payments");
        await ReadName(id);

        await Rename(id, "Billing and payments");

        Assert.Equal("Billing and payments", await ReadName(id));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    private async Task<Result<DepartmentId, Error>> Rename(Guid id, string name)
    {
        await using var scope = Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<RenameDepartmentHandler>();
        return await handler.Handle(new RenameDepartmentCommand(id, new RenameDepartmentRequest(name)), CancellationToken.None);
    }

    private async Task<string?> ReadName(Guid id)
    {
        await using var scope = Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetDepartmentByIdHandler>();
        var result = await handler.Handle(new GetDepartmentByIdRequest(id), CancellationToken.None);
        return result.Value?.Name;
    }

    private async Task<Guid> CreateDepartment(string name, string identifier)
    {
        var location = await InDb(async db =>
        {
            var created = new Location(
                LocationId.NewLocationId(),
                LocationName.Create("Head office").Value,
                Timezone.Create("europe/asia").Value,
                Address.Create("Main street", "Capital", "Country").Value,
                []);
            db.Locations.Add(created);
            await db.SaveChangesAsync();
            return created.Id;
        });

        await using var scope = Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<CreateDepartmentHandler>();
        var result = await handler.Handle(
            new CreateDepartmentCommand(new CreateDepartmentRequest(
                DepartmentName.Create(name).Value,
                DepartmentIdentifier.Create(identifier).Value,
                null,
                null,
                [location],
                DepartmentId.NewDepartmentId())),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task<T> InDb<T>(Func<DirectoryServiceDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>());
    }

    private async Task InDb(Func<DirectoryServiceDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>());
    }
}
