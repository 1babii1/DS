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
        // Arrange
        var cancellationToken = CancellationToken.None;
        var departmentIdHierarchy = await CreateDepartmentHierarchy(4);

        // Act
        var resultUpdate = await ExecuteHadler((sut) =>
        {
            var command =
                new UpdateParentDepartmentCommand(
                    departmentIdHierarchy[0].Value,
                    new UpdateParentDepartmentRequest(departmentIdHierarchy[2].Value));

            return sut.Handle(command, cancellationToken);
        });

        // Assert
        await ExecuteInDb(async dbContext =>
        {
            var department =
                await dbContext.Departments.FirstAsync(
                    d => d.Id == departmentIdHierarchy[0],
                    cancellationToken);

            Assert.NotNull(department);
            Assert.Equal(departmentIdHierarchy[2], department.ParentId);

            Assert.True(resultUpdate.IsSuccess);
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
        // Assert.Contains("not found", result.Error.Messages.Select(m => m.message), StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("You cannot designate yourself as a parent", result.Error.Messages.Select(m => m.message));
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

        // Assert.Contains("not found", result.Error.Messages.Select(m => m.message), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateParentDepartment_cycle_validation()
    {
        // Arrange - создаем цикл: A -> B -> C -> A
        var hierarchy = await CreateDepartmentHierarchy(3); // [A, B, C]
        var departmentA = hierarchy[0]; // A
        var departmentC = hierarchy[2]; // C

        // Меняем C чтобы parent был A (создает цикл)
        var command = new UpdateParentDepartmentCommand(
            departmentC.Value,
            new UpdateParentDepartmentRequest(departmentA.Value));

        // Act & Assert
        var result = await ExecuteHadler<Result<DepartmentId, Error>>((UpdateParentDepartmentHandler sut) =>
            sut.Handle(command, CancellationToken.None));

        Assert.True(result.IsFailure); // Должен упасть на цикле или depth<0

        // Assert.Contains("cycle", result.Error.Messages.Select(m => m.message), StringComparison.OrdinalIgnoreCase);
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
                                DepartmentIdentifier.Create($"level").Value,
                                DepartmentId.FromValue(parentId.Value),
                                null, [locationId], DepartmentId.NewDepartmentId())), CancellationToken.None);
                }

                return sut.Handle(
                    new CreateDepartmentCommand(
                        new CreateDepartmentRequest(
                            DepartmentName.Create($"Уровень{i}").Value,
                            DepartmentIdentifier.Create($"level").Value,
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