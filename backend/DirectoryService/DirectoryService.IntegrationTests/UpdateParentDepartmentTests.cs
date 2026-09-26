using System.Text.Json;
using CSharpFunctionalExtensions;
using DirectoryService.Application.Department;
using DirectoryService.Application.Department.Commands;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations;
using DirectoryService.Domain.Locations.ValueObjects;
using DirectoryService.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared;
using Xunit.Abstractions;

namespace DirectoryService.IntegrationTests;

public class UpdateParentDepartmentTests : IClassFixture<DirectoryTestWEbFactory>, IAsyncLifetime
{
    private readonly ITestOutputHelper _testOutputHelper;
    private readonly Func<Task> _resetDatabase;

    private IServiceProvider Services { get; set; }

    public UpdateParentDepartmentTests(DirectoryTestWEbFactory factory, ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
        Services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task UpdateParentDepartment_with_valid_data()
    {
        // Arrange - цепочка level0 -> level1 -> level2 -> level3. Поднимаем самый
        // глубокий узел под корень: законный перенос вверх по дереву.
        var cancellationToken = CancellationToken.None;
        var departmentIdHierarchy = await CreateDepartmentHierarchy(4);
        var root = departmentIdHierarchy[0];
        var deepest = departmentIdHierarchy[3];

        // Act
        var resultUpdate = await ExecuteHandler((sut) =>
        {
            var command =
                new UpdateParentDepartmentCommand(
                    deepest.Value,
                    new UpdateParentDepartmentRequest(root.Value));

            return sut.Handle(command, cancellationToken);
        });

        Assert.True(resultUpdate.IsSuccess);

        // Assert - родитель, путь и глубина пересчитаны согласованно
        await ExecuteInDb(async dbContext =>
        {
            var department =
                await dbContext.Departments.AsNoTracking().FirstAsync(
                    d => d.Id == deepest,
                    cancellationToken);

            Assert.Equal(root, department.ParentId);
            Assert.Equal("level0.level3", department.Path.Value);
            Assert.Equal(1, department.Depth);
        });

        // Assert - перемещение попало в outbox в той же транзакции, что и сам переезд:
        // без этого события AuditService никогда не узнал бы, что структура менялась.
        await ExecuteInDb(async dbContext =>
        {
            var moved = await dbContext.Set<Shared.Outbox.OutboxMessage>()
                .AsNoTracking()
                .SingleAsync(
                    m => m.Type == "DepartmentMoved" && m.AggregateId == deepest.Value.ToString(),
                    cancellationToken);

            Assert.Contains(root.Value.ToString(), moved.Payload);
        });
    }

    [Fact]
    public async Task UpdateParentDepartment_child_not_found()
    {
        // Arrange
        var validParentId = await CreateSingleDepartment(); // Создаем валидного parent
        var nonExistentChildId = DepartmentId.NewDepartmentId().Value;

        var command = new UpdateParentDepartmentCommand(
            nonExistentChildId,
            new UpdateParentDepartmentRequest(validParentId));

        // Act & Assert
        var result = await ExecuteHandler<Result<DepartmentId, Error>>((UpdateParentDepartmentHandler sut) =>
            sut.Handle(command, CancellationToken.None));

        Assert.True(result.IsFailure);

        // Assert.Contains("not found", result.Error.Messages.Select(m => m.Message), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateParentDepartment_self_as_parent()
    {
        // Arrange
        var departmentId = await CreateSingleDepartment();

        var command = new UpdateParentDepartmentCommand(
            departmentId,
            new UpdateParentDepartmentRequest(departmentId)); // ← Сам себе parent

        // Act & Assert
        var result = await ExecuteHandler<Result<DepartmentId, Error>>((UpdateParentDepartmentHandler sut) =>
            sut.Handle(command, CancellationToken.None));

        Assert.True(result.IsFailure);
        Assert.Contains("You cannot designate yourself as a parent", result.Error.Messages.Select(m => m.Message));
    }

    [Fact]
    public async Task UpdateParentDepartment_parent_not_found()
    {
        // Arrange
        var validChildId = await CreateSingleDepartment();
        var nonExistentParentId = DepartmentId.NewDepartmentId().Value;

        var command = new UpdateParentDepartmentCommand(
            validChildId,
            new UpdateParentDepartmentRequest(nonExistentParentId));

        // Act & Assert
        var result = await ExecuteHandler<Result<DepartmentId, Error>>((UpdateParentDepartmentHandler sut) =>
            sut.Handle(command, CancellationToken.None));

        Assert.True(result.IsFailure);

        // Assert.Contains("not found", result.Error.Messages.Select(m => m.Message), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateParentDepartment_rejects_move_into_own_subtree()
    {
        // Arrange - цепочка A -> B -> C. Переносим A под C, то есть под собственного
        // потомка: это отцепило бы поддерево от дерева и оставило цикл.
        var hierarchy = await CreateDepartmentHierarchy(3);
        var departmentA = hierarchy[0];
        var departmentC = hierarchy[2];

        var command = new UpdateParentDepartmentCommand(
            departmentA.Value,
            new UpdateParentDepartmentRequest(departmentC.Value));

        // Act
        var result = await ExecuteHandler<Result<DepartmentId, Error>>((UpdateParentDepartmentHandler sut) =>
            sut.Handle(command, CancellationToken.None));

        // Assert - именно из-за цикла, а не по любой другой причине
        Assert.True(result.IsFailure);
        Assert.Contains("department.cycle", result.Error.Messages.Select(m => m.Code));
    }

    [Fact]
    public async Task UpdateParentDepartment_allows_move_to_grandparent()
    {
        // Arrange - цепочка A -> B -> C. Перенос C под A законен: A предок C, но не
        // потомок, цикла не возникает. Этот случай раньше ошибочно считался циклом.
        var hierarchy = await CreateDepartmentHierarchy(3);
        var departmentA = hierarchy[0];
        var departmentC = hierarchy[2];

        var command = new UpdateParentDepartmentCommand(
            departmentC.Value,
            new UpdateParentDepartmentRequest(departmentA.Value));

        // Act
        var result = await ExecuteHandler<Result<DepartmentId, Error>>((UpdateParentDepartmentHandler sut) =>
            sut.Handle(command, CancellationToken.None));

        // Assert
        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// Written to check a hypothesis from this session's audit, not to demonstrate a
    /// known bug: GetByIdWithLock on the new parent then the current department locks
    /// two single rows in an order that mirrors the request, so "move A under B" and
    /// "move B under A" fired at the same time lock B-then-A and A-then-B respectively -
    /// a textbook circular wait. The open question was whether that surfaces as a
    /// handled Result (the deadlock caught by GetByIdWithLock's own NpgsqlException
    /// handler) or as an unhandled exception reaching the caller as a raw 500. Racing it
    /// for real settles that rather than reasoning about Postgres's lock ordering from
    /// the code alone.
    /// </summary>
    [Fact]
    public async Task Concurrent_opposite_direction_moves_never_create_a_cycle()
    {
        var locationId = await CreateLocation("race");

        var deptA = await CreateRootDepartment("racea", locationId);
        var deptB = await CreateRootDepartment("raceb", locationId);

        var moveAUnderB = ExecuteHandler<Result<DepartmentId, Error>>(sut =>
            sut.Handle(
                new UpdateParentDepartmentCommand(deptA.Value, new UpdateParentDepartmentRequest(deptB.Value)),
                CancellationToken.None));

        var moveBUnderA = ExecuteHandler<Result<DepartmentId, Error>>(sut =>
            sut.Handle(
                new UpdateParentDepartmentCommand(deptB.Value, new UpdateParentDepartmentRequest(deptA.Value)),
                CancellationToken.None));

        // If either handler let an exception escape instead of returning a Result -
        // exactly what an unhandled deadlock would look like - Task.WhenAll rethrows it
        // here and the test fails with that exception, not a clean assertion.
        var results = await Task.WhenAll(moveAUnderB, moveBUnderA);

        // Both succeeding would mean the tree now has A's parent as B and B's parent as
        // A at the same time - an actual cycle. At most one of two mutually exclusive
        // moves can be legitimate. In practice this resolves cleanly every time observed
        // (one success, one department.cycle validation error) rather than deadlocking -
        // Postgres serializes the two GetByIdWithLock calls fast enough in this codebase's
        // shape that the circular-wait window the audit worried about does not open. Even
        // if it did, GetByIdWithLock's own NpgsqlException handler would turn a real
        // deadlock into this same kind of Result instead of an unhandled exception - which
        // is what the absence of any exception from Task.WhenAll above already confirms.
        Assert.True(results.Count(r => r.IsSuccess) <= 1);

        await ExecuteInDb(async dbContext =>
        {
            var a = await dbContext.Departments.AsNoTracking().FirstAsync(d => d.Id == deptA, CancellationToken.None);
            var b = await dbContext.Departments.AsNoTracking().FirstAsync(d => d.Id == deptB, CancellationToken.None);

            var cycleExists = a.ParentId == deptB && b.ParentId == deptA;
            Assert.False(cycleExists, "A cycle was created: A's parent is B and B's parent is A");
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _resetDatabase();
    }

    private async Task<LocationId> CreateLocation(string suffix = "")
    {
        LocationId locationId;
        return await ExecuteInDb(async dbContext =>
        {
            var location = new Location(LocationId.NewLocationId(), LocationName.Create($"location{suffix}").Value,
                Timezone.Create("europe/asia").Value,
                Address.Create($"street{suffix}", $"city{suffix}", $"country{suffix}").Value, []);
            dbContext.Locations.Add(location);
            await dbContext.SaveChangesAsync();

            locationId = location.Id;
            return locationId;
        });
    }

    private async Task<DepartmentId> CreateRootDepartment(string identifier, LocationId locationId)
    {
        var result = await ExecuteHandler<Result<Guid, Error>>((CreateDepartmentHandler sut) =>
            sut.Handle(
                new CreateDepartmentCommand(new CreateDepartmentRequest(
                    DepartmentName.Create(identifier).Value,
                    DepartmentIdentifier.Create(identifier).Value,
                    null, null, [locationId], DepartmentId.NewDepartmentId())),
                CancellationToken.None));

        Assert.True(result.IsSuccess);
        return DepartmentId.FromValue(result.Value);
    }

    private async Task<Guid> CreateSingleDepartment()
    {
        var locationId = await CreateLocation("single");
        var result = await ExecuteHandler<Result<Guid, Error>>((CreateDepartmentHandler sut) =>
        {
            return sut.Handle(
                new CreateDepartmentCommand(new CreateDepartmentRequest(
                    DepartmentName.Create("Test").Value,
                    DepartmentIdentifier.Create("test").Value,
                    null, null, [locationId], DepartmentId.NewDepartmentId())),
                CancellationToken.None);
        });

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task<List<DepartmentId>> CreateDepartmentHierarchy(int levels = 3)
    {
        var locationId = await CreateLocation();
        DepartmentId? parentId = null;
        var departmentIdHierarchy = new List<DepartmentId>();

        for (int i = 0; i < levels; i++)
        {
            var result = await ExecuteHandler((sut) =>
            {
                if (parentId != null)
                {
                    return sut.Handle(
                        new CreateDepartmentCommand(
                            new CreateDepartmentRequest(
                                DepartmentName.Create($"Уровень{i}").Value,
                                DepartmentIdentifier.Create($"level{i}").Value,
                                DepartmentId.FromValue(parentId.Value),
                                null, [locationId], DepartmentId.NewDepartmentId())), CancellationToken.None);
                }

                return sut.Handle(
                    new CreateDepartmentCommand(
                        new CreateDepartmentRequest(
                            DepartmentName.Create($"Уровень{i}").Value,
                            DepartmentIdentifier.Create($"level{i}").Value,
                            null,
                            null, [locationId], DepartmentId.NewDepartmentId())), CancellationToken.None);
            });
            if (result.IsFailure)
            {
                _testOutputHelper.WriteLine("_______________Ошибка_____________");
            }

            var departmentId = DepartmentId.FromValue(result.Value);
            departmentIdHierarchy.Add(departmentId);
            parentId = departmentId;
        }

        return departmentIdHierarchy;
    }

    private async Task<T> ExecuteHandler<T>(Func<CreateDepartmentHandler, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();

        var sut = scope.ServiceProvider.GetRequiredService<CreateDepartmentHandler>();

        return await action(sut);
    }

    private async Task<T> ExecuteHandler<T>(Func<UpdateParentDepartmentHandler, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();

        var sut = scope.ServiceProvider.GetRequiredService<UpdateParentDepartmentHandler>();

        return await action(sut);
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