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

public class CreateDirectoryTests : IClassFixture<DirectoryTestWEbFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;

    private IServiceProvider Services { get; set; }

    public CreateDirectoryTests(DirectoryTestWEbFactory factory)
    {
        Services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task CreateDepartment_with_valid_data()
    {
        // arrange
        var locationId = await CreateLocation();
        var cancellationToken = CancellationToken.None;

        // act
        var result = await ExecuteHadler((sut) =>
        {
            var command =
                new CreateDepartmentCommand(new CreateDepartmentRequest(
                    DepartmentName.Create("подразделение").Value,
                    DepartmentIdentifier.Create("podrazdelenie").Value,
                    null,
                    null,
                    [locationId], DepartmentId.NewDepartmentId()));

            return sut.Handle(command, cancellationToken);
        });

        // assert
        await ExecuteInDb(async dbContext =>
        {
            var department =
                await dbContext.Departments.FirstAsync(
                    d => d.Id == DepartmentId.FromValue(result.Value),
                    cancellationToken);

            Assert.NotNull(department);
            Assert.Equal(department.Id.Value, result.Value);

            Assert.True(result.IsSuccess);
            Assert.NotEqual(Guid.Empty, result.Value);
        });
    }

    [Fact]
    public async Task CreateDepartment_with_invalid_locationId()
    {
        // arrange
        var cancellationToken = CancellationToken.None;

        // act
        var result = await ExecuteHadler((sut) =>
        {
            var command =
                new CreateDepartmentCommand(new CreateDepartmentRequest(
                    DepartmentName.Create("подразделение").Value,
                    DepartmentIdentifier.Create("podrazdelenie").Value,
                    null,
                    null,
                    [LocationId.NewLocationId()], DepartmentId.NewDepartmentId()));

            return sut.Handle(command, cancellationToken);
        });

        // assert
        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task CreateDepartment_with_without_locationId()
    {
        // arrange
        var cancellationToken = CancellationToken.None;

        // act
        var result = await ExecuteHadler((sut) =>
        {
            var command =
                new CreateDepartmentCommand(new CreateDepartmentRequest(
                    DepartmentName.Create("подразделение").Value,
                    DepartmentIdentifier.Create("podrazdelenie").Value,
                    null,
                    null,
                    [], DepartmentId.NewDepartmentId()));

            return sut.Handle(command, cancellationToken);
        });

        // assert
        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task CreateDepartment_with_lenght_less_three_name_and_identifier()
    {
        // arrange
        var locationId = await CreateLocation();
        var cancellationToken = CancellationToken.None;

        // act
        var result = await ExecuteHadler((sut) =>
        {
            var command =
                new CreateDepartmentCommand(new CreateDepartmentRequest(
                    DepartmentName.Create("по").Value,
                    DepartmentIdentifier.Create("po").Value,
                    null,
                    null,
                    [locationId], DepartmentId.NewDepartmentId()));

            return sut.Handle(command, cancellationToken);
        });

        // assert
        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task CreateDepartment_with_lenght_more_150_name_and_identifier()
    {
        // arrange
        var locationId = await CreateLocation();
        var cancellationToken = CancellationToken.None;

        // act
        var result = await ExecuteHadler((sut) =>
        {
            var command =
                new CreateDepartmentCommand(new CreateDepartmentRequest(
                    DepartmentName
                        .Create("подразделениекогдаоткрыливсеникакнеможемзакрытьноможетбытькогданибудьзакроемноэтонеточноведьсейчастяжелоевремяиниктонезнаетчтобудетзавтраазавтраможетслучитьсявсечтоугодно").Value,
                    DepartmentIdentifier.Create("podrazdeleniekogdaotkrylivsenikaknemozhemzakryt'nomozhetbyt'kogdanibud'zakroemnoetonetochnoved'sejchastyazheloevremyainiktoneznaetchtobudetzavtraazavtramozhetsluchit'syavsechtougodno").Value,
                    null,
                    null,
                    [locationId], null ));

            return sut.Handle(command, cancellationToken);
        });

        // assert
        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
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
            var location = new Locations(LocationId.NewLocationId(), LocationName.Create("location").Value,
                Timezone.Create("europe/asia").Value, Address.Create($"street{suffix}", $"city{suffix}", $"country{suffix}").Value, []);
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