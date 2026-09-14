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
        var result = await ExecuteHandler((sut) =>
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
        var result = await ExecuteHandler((sut) =>
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
        var result = await ExecuteHandler((sut) =>
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

    /// <summary>
    /// Written to check a hypothesis from this session's audit, not to demonstrate a
    /// known bug: CreateDepartmentHandler never looks up an existing sibling before
    /// inserting, and Path has no unique constraint or index in
    /// DepartmentConfigurations - only a GiST index for query performance. Path is
    /// built as parent.path + "." + identifier, so two children of the same parent
    /// (or two roots) with the same identifier would get an identical path, which the
    /// ltree hierarchy assumes can never happen.
    /// </summary>
    [Fact]
    public async Task CreateDepartment_with_a_duplicate_identifier_is_rejected()
    {
        var locationId = await CreateLocation("dup");

        var first = await ExecuteHandler(sut => sut.Handle(
            new CreateDepartmentCommand(new CreateDepartmentRequest(
                DepartmentName.Create("First").Value,
                DepartmentIdentifier.Create("dupident").Value,
                null, null, [locationId], DepartmentId.NewDepartmentId())),
            CancellationToken.None));
        Assert.True(first.IsSuccess);

        var second = await ExecuteHandler(sut => sut.Handle(
            new CreateDepartmentCommand(new CreateDepartmentRequest(
                DepartmentName.Create("Second").Value,
                DepartmentIdentifier.Create("dupident").Value,
                null, null, [locationId], DepartmentId.NewDepartmentId())),
            CancellationToken.None));

        Assert.True(second.IsFailure, "A second root department with the same identifier should not be allowed to collide on path with the first");
    }

    // Длина проверяется в value object, поэтому команду с некорректным именем
    // собрать нельзя в принципе - правило проверяется там, где оно живёт.
    [Fact]
    public void DepartmentName_and_identifier_shorter_than_three_are_rejected()
    {
        Assert.True(DepartmentName.Create("по").IsFailure);
        Assert.True(DepartmentIdentifier.Create("po").IsFailure);
    }

    [Fact]
    public void DepartmentName_and_identifier_longer_than_150_are_rejected()
    {
        var tooLongName = new string('я', 151);
        var tooLongIdentifier = new string('a', 151);

        Assert.True(DepartmentName.Create(tooLongName).IsFailure);
        Assert.True(DepartmentIdentifier.Create(tooLongIdentifier).IsFailure);
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

    private async Task<T> ExecuteHandler<T>(Func<CreateDepartmentHandler, Task<T>> action)
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