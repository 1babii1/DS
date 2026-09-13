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
        var resultUpdate = await ExecuteHadler((sut) =>
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
        var result = await ExecuteHadler<Result<DepartmentId, Error>>((UpdateParentDepartmentHandler sut) =>
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
        var result = await ExecuteHadler<Result<DepartmentId, Error>>((UpdateParentDepartmentHandler sut) =>
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
        var result = await ExecuteHadler<Result<DepartmentId, Error>>((UpdateParentDepartmentHandler sut) =>
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
        var result = await ExecuteHadler<Result<DepartmentId, Error>>((UpdateParentDepartmentHandler sut) =>
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
        var result = await ExecuteHadler<Result<DepartmentId, Error>>((UpdateParentDepartmentHandler sut) =>
            sut.Handle(command, CancellationToken.None));

        // Assert
        Assert.True(result.IsSuccess);
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
            var location = new Locations(LocationId.NewLocationId(), LocationName.Create($"location{suffix}").Value,
                Timezone.Create("europe/asia").Value,
                Address.Create($"street{suffix}", $"city{suffix}", $"country{suffix}").Value, []);
            dbContext.Locations.Add(location);
            await dbContext.SaveChangesAsync();

            locationId = location.Id;
            return locationId;
        });
    }

    private async Task<Guid> CreateSingleDepartment()
    {
        var locationId = await CreateLocation("single");
        var result = await ExecuteHadler<Result<Guid, Error>>((CreateDepartmentHandler sut) =>
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
            var result = await ExecuteHadler((sut) =>
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

    private async Task<T> ExecuteHadler<T>(Func<CreateDepartmentHandler, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();

        var sut = scope.ServiceProvider.GetRequiredService<CreateDepartmentHandler>();

        return await action(sut);
    }

    private async Task<T> ExecuteHadler<T>(Func<UpdateParentDepartmentHandler, Task<T>> action)
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