using System.Net;
using System.Net.Http.Json;
using DirectoryService.Domain.DepartmentLocations;
using DirectoryService.Domain.Departments;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations;
using DirectoryService.Domain.Locations.ValueObjects;
using DirectoryService.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Shared;

namespace DirectoryService.IntegrationTests;

/// <summary>
/// Page size on the catalogue queries reaches SQL's LIMIT directly. Two of these endpoints
/// used to pass it through unchecked, which meant an omitted value became "LIMIT NULL" -
/// no limit at all in Postgres - and a large one returned the whole table in one response.
/// <para>
/// Seeding deliberately exceeds <see cref="PagedResponse{T}.MaxSize"/>: with fewer rows
/// than the cap, a clamped query and an unclamped one return exactly the same page, so the
/// assertions would pass against the bug they exist to catch.
/// </para>
/// <para>
/// Driven over real HTTP because the interesting input - "?pageSize=" with no value, which
/// binds to null - only exists at the model-binding layer.
/// </para>
/// </summary>
public class PaginationBoundsTests : IClassFixture<DirectoryTestWEbFactory>, IAsyncLifetime
{
    private const int SeededRows = PagedResponse<object>.MaxSize + 50;

    private readonly DirectoryTestWEbFactory _factory;
    private readonly Func<Task> _resetDatabase;

    public PaginationBoundsTests(DirectoryTestWEbFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    public async Task InitializeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>();

        for (var i = 0; i < SeededRows; i++)
        {
            var location = new Location(
                LocationId.NewLocationId(),
                LocationName.Create($"location{i:D4}").Value,
                Timezone.Create("europe/asia").Value,
                Address.Create($"street{i}", $"city{i}", $"country{i}").Value,
                []);

            var department = Department.CreateParent(
                DepartmentName.Create($"department{i:D4}").Value,
                DepartmentIdentifier.Create($"dep{i:D4}").Value,
                [location.Id]).Value;

            dbContext.Locations.Add(location);
            dbContext.Departments.Add(department);
            dbContext.DepartmentLocations.Add(
                DepartmentLocation.Create(null, department.Id, location.Id).Value);
        }

        await dbContext.SaveChangesAsync();
    }

    public Task DisposeAsync() => _resetDatabase();

    [Theory]
    [InlineData("/api/locations/by-department")]
    [InlineData("/api/departments/department/location")]
    public async Task An_omitted_page_size_does_not_disable_the_limit(string path)
    {
        using var client = _factory.CreateClient();

        // "?pageSize=" binds to null on a property-initialised request record, which is how
        // this became LIMIT NULL - Postgres reads a null limit as "no limit at all".
        var response = await client.GetAsync($"{path}?pageSize=&page=");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertRowsAreCapped(response, path);
    }

    [Theory]
    [InlineData("/api/locations/by-department")]
    [InlineData("/api/departments/department/location")]
    public async Task An_enormous_page_size_is_clamped_instead_of_dumping_the_table(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"{path}?pageSize={int.MaxValue}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertRowsAreCapped(response, path);
    }

    [Theory]
    [InlineData("/api/locations/by-department")]
    [InlineData("/api/departments/department/location")]
    public async Task A_negative_page_size_does_not_reach_sql(string path)
    {
        using var client = _factory.CreateClient();

        // Postgres rejects a negative LIMIT outright, so an unclamped value surfaced as a
        // 500 on an otherwise harmless request.
        var response = await client.GetAsync($"{path}?pageSize=-5&page=-1");

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/locations")]
    [InlineData("/api/positions")]
    public async Task Catalogue_endpoints_report_the_clamped_size_they_actually_used(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"{path}?size={int.MaxValue}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<PagedResponse<object>>();
        Assert.NotNull(page);
        Assert.Equal(PagedResponse<object>.MaxSize, page!.Size);
        Assert.True(page.Items.Count <= PagedResponse<object>.MaxSize);
    }

    [Fact]
    public async Task Locations_catalogue_does_not_return_the_whole_table_when_size_is_omitted()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/locations?size=&page=");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<PagedResponse<object>>();
        Assert.NotNull(page);
        Assert.True(page!.Items.Count <= PagedResponse<object>.MaxSize);
        Assert.Equal(SeededRows, page.Total);
    }

    [Fact]
    public async Task Roots_rejects_an_oversized_page_even_when_page_itself_is_absent()
    {
        using var client = _factory.CreateClient();

        // The size bound used to be scoped on a guard over Page, so leaving Page out was
        // enough to drop it entirely.
        var response = await client.GetAsync($"/api/departments/roots?size={int.MaxValue}");

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // These two endpoints answer with a bare list rather than a PagedResponse, so the clamp
    // is observable only as the row count.
    private static async Task AssertRowsAreCapped(HttpResponseMessage response, string path)
    {
        var items = await response.Content.ReadFromJsonAsync<List<object>>();

        Assert.NotNull(items);
        Assert.True(
            items!.Count <= PagedResponse<object>.MaxSize,
            $"{path} returned {items.Count} rows against {SeededRows} seeded - the LIMIT was not applied");
    }
}
