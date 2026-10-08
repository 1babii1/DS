using EmployeeService.Application.Authorization;
using EmployeeService.Web.Authorization;
using Microsoft.Extensions.Options;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace EmployeeService.IntegrationTests;

// The model and the client against a real OpenFGA (ADR 0057): what the in-memory stand-in only imitates. The server is the one the platform runs
// (in-memory store, as in the development profile); the model is the one embedded in the service, which scripts/check-fga-model.sh keeps equal to the model its own tests are written for.
public class OpenFgaStoreTests : IAsyncLifetime
{
    private IContainer? _server;
    private HttpClient? _http;
    private OpenFgaHttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _server = new ContainerBuilder("openfga/openfga:v1.22.0")
            .WithCommand("run")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8080).ForPath("/healthz")))
            .Build();
        await _server.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://{_server.Hostname}:{_server.GetMappedPublicPort(8080)}"), Timeout = TimeSpan.FromSeconds(10) };
        _client = new OpenFgaHttpClient(_http, Options.Create(new DepartmentAuthorizationOptions { StoreName = $"test-{Guid.NewGuid():N}" }), new OpenFgaStoreState());
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_store_and_the_model_are_created_on_first_use_and_the_model_does_what_its_tests_say()
    {
        var company = Guid.NewGuid();
        var engineering = Guid.NewGuid();
        var backend = Guid.NewGuid();
        var anna = Guid.NewGuid();
        var boris = Guid.NewGuid();
        await _client.WriteAsync(
            [
                new(FgaTuple.DepartmentId(company), "parent", FgaTuple.DepartmentId(engineering)),
                new(FgaTuple.DepartmentId(engineering), "parent", FgaTuple.DepartmentId(backend)),
                new(FgaTuple.UserId(anna), "manager", FgaTuple.DepartmentId(engineering)),
            ],
            [],
            CancellationToken.None);

        Assert.True(await Can(anna, engineering));
        Assert.True(await Can(anna, backend));
        Assert.False(await Can(anna, company));
        Assert.False(await Can(boris, backend));
    }

    [Fact]
    public async Task A_right_given_is_seen_by_the_very_next_check_and_one_taken_away_is_gone_at_once()
    {
        var department = Guid.NewGuid();
        var account = Guid.NewGuid();
        var tuple = new FgaTuple(FgaTuple.UserId(account), "manager", FgaTuple.DepartmentId(department));
        Assert.False(await Can(account, department));

        await _client.WriteAsync([tuple], [], CancellationToken.None);
        Assert.True(await Can(account, department));

        await _client.WriteAsync([], [tuple], CancellationToken.None);
        Assert.False(await Can(account, department));
    }

    [Fact]
    public async Task Writing_what_is_there_and_deleting_what_is_not_are_not_errors_because_events_arrive_twice()
    {
        var department = Guid.NewGuid();
        var tuple = new FgaTuple(FgaTuple.UserId(Guid.NewGuid()), "manager", FgaTuple.DepartmentId(department));

        await _client.WriteAsync([tuple], [], CancellationToken.None);
        await _client.WriteAsync([tuple], [], CancellationToken.None);
        await _client.WriteAsync([], [tuple], CancellationToken.None);
        await _client.WriteAsync([], [tuple], CancellationToken.None);

        Assert.Empty(await _client.ReadAsync(FgaTuple.DepartmentId(department), null, CancellationToken.None));
    }

    [Fact]
    public async Task A_move_through_the_real_store_changes_who_may_manage_in_one_step()
    {
        var company = Guid.NewGuid();
        var engineering = Guid.NewGuid();
        var sales = Guid.NewGuid();
        var team = Guid.NewGuid();
        var anna = Guid.NewGuid();
        var carla = Guid.NewGuid();
        var sync = new DepartmentTreeSync(_client);
        await sync.OnCreated(engineering, company, CancellationToken.None);
        await sync.OnCreated(sales, company, CancellationToken.None);
        await sync.OnCreated(team, engineering, CancellationToken.None);
        await _client.WriteAsync(
            [new(FgaTuple.UserId(anna), "manager", FgaTuple.DepartmentId(engineering)), new(FgaTuple.UserId(carla), "manager", FgaTuple.DepartmentId(sales))],
            [],
            CancellationToken.None);
        Assert.True(await Can(anna, team));
        Assert.False(await Can(carla, team));

        await sync.OnMoved(team, sales, CancellationToken.None);
        await sync.OnMoved(team, sales, CancellationToken.None);

        Assert.False(await Can(anna, team));
        Assert.True(await Can(carla, team));
        var parents = await _client.ReadAsync(FgaTuple.DepartmentId(team), "parent", CancellationToken.None);
        Assert.Equal([FgaTuple.DepartmentId(sales)], parents.Select(p => p.User));
    }

    [Fact]
    public async Task Deleting_a_department_through_the_real_store_removes_its_parent_and_its_managers()
    {
        var parent = Guid.NewGuid();
        var department = Guid.NewGuid();
        var account = Guid.NewGuid();
        var sync = new DepartmentTreeSync(_client);
        await sync.OnCreated(department, parent, CancellationToken.None);
        await _client.WriteAsync([new(FgaTuple.UserId(account), "manager", FgaTuple.DepartmentId(department))], [], CancellationToken.None);

        await sync.OnDeleted(department, CancellationToken.None);

        Assert.Empty(await _client.ReadAsync(FgaTuple.DepartmentId(department), null, CancellationToken.None));
        Assert.False(await Can(account, department));
    }

    [Fact]
    public async Task A_server_that_is_not_there_is_an_error_the_decision_turns_into_a_refusal()
    {
        using var nowhere = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1"), Timeout = TimeSpan.FromSeconds(2) };
        var client = new OpenFgaHttpClient(nowhere, Options.Create(new DepartmentAuthorizationOptions()), new OpenFgaStoreState());

        await Assert.ThrowsAsync<FgaException>(() => client.CheckAsync(new FgaTuple("user:a", "can_manage", "department:b"), CancellationToken.None));

        var decision = await new TreeDepartmentAuthorization(client, Microsoft.Extensions.Logging.Abstractions.NullLogger<TreeDepartmentAuthorization>.Instance)
            .CanManageAsync(new Caller(Guid.NewGuid(), false), Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(AccessDecision.Unavailable, decision);
    }

    private Task<bool> Can(Guid account, Guid department) =>
        _client.CheckAsync(new FgaTuple(FgaTuple.UserId(account), "can_manage", FgaTuple.DepartmentId(department)), CancellationToken.None);
}
