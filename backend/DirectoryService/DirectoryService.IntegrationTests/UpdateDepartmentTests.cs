using DirectoryService.Application.Department;
using DirectoryService.Application.Department.Commands;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations;
using DirectoryService.Domain.Locations.ValueObjects;
using DirectoryService.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DirectoryService.IntegrationTests;

public class UpdateDepartmentTests : IClassFixture<DirectoryTestWEbFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;

    private IServiceProvider Services { get; set; }

    public UpdateDepartmentTests(DirectoryTestWEbFactory factory)
    {
        Services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task UpdateParentDepartment_with_valid_data()
    {
        // Arrange
        var locationIdFirst = await CreateLocation();
        var cancellationToken = CancellationToken.None;
        var result = await ExecuteHadler((sut) =>
        {
            var command =
                new CreateDepartmentCommand(new CreateDepartmentRequest(
                    DepartmentName.Create("подразделение").Value,
                    DepartmentIdentifier.Create("podrazdelenie").Value,
                    null,
                    null,
                    [locationIdFirst], DepartmentId.NewDepartmentId()));

            return sut.Handle(command, cancellationToken);
        });

        var locationIdSecond = await CreateLocation("1");

        // Act
        var resultUpdate = await ExecuteHadler((sut) =>
        {
            var command =
                new UpdateDepartmentLocationsCommand(new UpdateDepartmentLocationsRequest(
                    DepartmentId.FromValue(result.Value),
                    [locationIdSecond]));

            return sut.Handle(command, cancellationToken);
        });

        // Assert
        await ExecuteInDb(async dbContext =>
        {
            var department =
                await dbContext.Departments.Include(departments => departments.DepartmentsLocationsList).FirstAsync(
                    d => d.Id == DepartmentId.FromValue(result.Value),
                    cancellationToken);

            Assert.NotNull(department);
            Assert.Single(department.DepartmentsLocationsList);
            Assert.Equal(locationIdSecond.Value, department.DepartmentsLocationsList.First().LocationId.Value);

            Assert.True(resultUpdate.IsSuccess);
            Assert.NotEqual(Guid.Empty, result.Value);
            Assert.NotEqual(Guid.Empty, resultUpdate.Value.Value);
        });
    }

    private async Task<Guid> CreateDepartmentWithLocation()
    {
        var locationId = await CreateLocation("dept");
        var result = await ExecuteHadler((CreateDepartmentHandler sut) =>
        {
            return sut.Handle(
                new CreateDepartmentCommand(new CreateDepartmentRequest(
                    DepartmentName.Create("TestDept").Value,
                    DepartmentIdentifier.Create("testdept").Value,
                    null, null, [locationId], DepartmentId.NewDepartmentId())),
                CancellationToken.None);
        });
        return result.Value;
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

    private async Task<T> ExecuteHadler<T>(Func<CreateDepartmentHandler, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();

        var sut = scope.ServiceProvider.GetRequiredService<CreateDepartmentHandler>();

        return await action(sut);
    }

    private async Task<T> ExecuteHadler<T>(Func<UpdateDepartmentLocationsHadler, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();

        var sut = scope.ServiceProvider.GetRequiredService<UpdateDepartmentLocationsHadler>();

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