using System.Security.Claims;
using EmployeeService.Application.Authorization;
using EmployeeService.Application.Database;
using EmployeeService.Application.Employees.Commands;
using EmployeeService.Domain;
using EmployeeService.Infrastructure.Postgres;
using EmployeeService.Web.Authorization;
using EmployeeService.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shared;
using Shared.EndpointResults;

namespace EmployeeService.IntegrationTests;

// Who may hire into, and move people between, which department (ADR 0057). The decisions and the tree's upkeep run against a small in-memory
// stand-in for the store that answers the one question the model asks; the same questions are asked of a real OpenFGA in OpenFgaStoreTests.
public class DepartmentAuthorizationTests : IClassFixture<EmployeeTestWebFactory>, IAsyncLifetime
{
    private readonly EmployeeTestWebFactory _baseFactory;
    private readonly Func<Task> _resetDatabase;
    private readonly FakeFga _store = new();
    private readonly IServiceProvider _services;

    private readonly Guid _company = Guid.NewGuid();
    private readonly Guid _engineering = Guid.NewGuid();
    private readonly Guid _backend = Guid.NewGuid();
    private readonly Guid _sales = Guid.NewGuid();
    private readonly Guid _anna = Guid.NewGuid();
    private readonly Guid _boris = Guid.NewGuid();

    public DepartmentAuthorizationTests(EmployeeTestWebFactory factory)
    {
        _baseFactory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
        _services = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IFgaClient>(_store);
            services.AddScoped<IDepartmentAuthorization, TreeDepartmentAuthorization>();
            services.AddScoped<DepartmentTreeSync>();
        })).Services;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    // company > engineering > backend, company > sales. Anna manages engineering, Boris manages backend.
    private async Task GrowTree()
    {
        await Sync(sync => sync.OnCreated(_company, null, CancellationToken.None));
        await Sync(sync => sync.OnCreated(_engineering, _company, CancellationToken.None));
        await Sync(sync => sync.OnCreated(_backend, _engineering, CancellationToken.None));
        await Sync(sync => sync.OnCreated(_sales, _company, CancellationToken.None));
        _store.Add(FgaTuple.UserId(_anna), "manager", FgaTuple.DepartmentId(_engineering));
        _store.Add(FgaTuple.UserId(_boris), "manager", FgaTuple.DepartmentId(_backend));
    }

    // ---- the decision ----

    [Fact]
    public async Task A_manager_may_manage_their_department_and_every_one_below_it_and_no_other()
    {
        await GrowTree();

        Assert.Equal(AccessDecision.Allowed, await Decide(_anna, _engineering));
        Assert.Equal(AccessDecision.Allowed, await Decide(_anna, _backend));
        Assert.Equal(AccessDecision.Allowed, await Decide(_boris, _backend));
        Assert.Equal(AccessDecision.Denied, await Decide(_boris, _engineering));
        Assert.Equal(AccessDecision.Denied, await Decide(_anna, _sales));
        Assert.Equal(AccessDecision.Denied, await Decide(Guid.NewGuid(), _backend));
    }

    [Fact]
    public async Task An_administrator_is_allowed_without_the_store_being_asked_and_an_anonymous_caller_is_not()
    {
        _store.Down = true;

        Assert.Equal(AccessDecision.Allowed, await Decide(null, _backend, isAdmin: true));
        Assert.Equal(AccessDecision.Denied, await Decide(null, _backend));
        Assert.Equal(0, _store.Checks);
    }

    [Fact]
    public async Task When_the_store_cannot_answer_a_non_administrator_is_refused_and_told_it_is_unavailable_not_denied()
    {
        await GrowTree();
        _store.Down = true;

        Assert.Equal(AccessDecision.Unavailable, await Decide(_anna, _backend));
    }

    [Fact]
    public async Task Roles_mode_adds_no_condition()
    {
        Assert.Equal(AccessDecision.Allowed, await new RolesDepartmentAuthorization().CanManageAsync(new Caller(null, false), _backend, CancellationToken.None));
    }

    // ---- the tree's upkeep ----

    [Fact]
    public async Task Moving_a_department_gives_the_new_ancestors_their_rights_and_takes_the_old_ones_away()
    {
        await GrowTree();
        Assert.Equal(AccessDecision.Allowed, await Decide(_anna, _backend));

        await Sync(sync => sync.OnMoved(_backend, _sales, CancellationToken.None));

        Assert.Equal(AccessDecision.Denied, await Decide(_anna, _backend));
        Assert.Equal(AccessDecision.Allowed, await Decide(_boris, _backend));
        Assert.Single(_store.Tuples.Where(t => t.Relation == "parent" && t.Object == FgaTuple.DepartmentId(_backend)));
    }

    [Fact]
    public async Task A_move_replaces_what_the_store_holds_not_what_the_event_remembers()
    {
        // The store has the department under two parents (an earlier event arrived twice, or out of order): the move leaves one.
        await GrowTree();
        _store.Add(FgaTuple.DepartmentId(_sales), "parent", FgaTuple.DepartmentId(_backend));

        await Sync(sync => sync.OnMoved(_backend, _company, CancellationToken.None));

        var parents = _store.Tuples.Where(t => t.Relation == "parent" && t.Object == FgaTuple.DepartmentId(_backend)).ToList();
        Assert.Equal([FgaTuple.DepartmentId(_company)], parents.Select(p => p.User));
    }

    [Fact]
    public async Task Events_delivered_twice_change_nothing_the_second_time()
    {
        await GrowTree();
        var before = _store.Tuples.ToHashSet();

        await Sync(sync => sync.OnCreated(_backend, _engineering, CancellationToken.None));
        await Sync(sync => sync.OnMoved(_backend, _engineering, CancellationToken.None));

        Assert.True(before.SetEquals(_store.Tuples));
    }

    [Fact]
    public async Task A_deleted_department_takes_its_parent_link_and_its_managers_with_it()
    {
        await GrowTree();

        await Sync(sync => sync.OnDeleted(_backend, CancellationToken.None));

        Assert.DoesNotContain(_store.Tuples, t => t.Object == FgaTuple.DepartmentId(_backend));
        Assert.Contains(_store.Tuples, t => t.Object == FgaTuple.DepartmentId(_engineering));
    }

    // ---- hire and transfer ----

    [Fact]
    public async Task A_manager_hires_into_their_own_tree_and_is_refused_elsewhere_without_anything_being_written()
    {
        await GrowTree();

        var inside = await Hire(_boris, _backend);
        var outside = await Hire(_boris, _sales);

        Assert.True(inside.IsSuccess);
        Assert.True(outside.IsFailure);
        Assert.Equal("employee.department.not_manager", outside.Error.Messages[0].Code);
        Assert.Equal(1, await CountEmployees());
    }

    [Fact]
    public async Task A_hire_while_the_store_is_down_is_refused_as_unavailable_and_writes_nothing_but_an_administrator_still_hires()
    {
        await GrowTree();
        _store.Down = true;

        var manager = await Hire(_boris, _backend);
        var admin = await Hire(_boris, _backend, isAdmin: true);

        Assert.True(manager.IsFailure);
        Assert.Equal("employee.authorization.unavailable", manager.Error.Messages[0].Code);
        Assert.True(admin.IsSuccess);
        Assert.Equal(1, await CountEmployees());
    }

    [Fact]
    public async Task The_caller_is_not_asked_about_a_department_that_does_not_exist_so_it_cannot_probe_for_one()
    {
        await GrowTree();

        var result = await Hire(_boris, Guid.NewGuid());

        Assert.Equal("employee.department.not_manager", result.Error.Messages[0].Code);
    }

    [Fact]
    public async Task A_transfer_takes_the_right_over_both_the_department_left_and_the_department_entered()
    {
        await GrowTree();
        var employee = (await Hire(_anna, _backend)).Value;
        await Activate(employee);

        // Boris manages backend, where the person is, but not sales, where they would go.
        var intoSales = await Transfer(_boris, employee, _sales);
        // Anna manages engineering above backend, but sales is outside her tree.
        var annaIntoSales = await Transfer(_anna, employee, _sales);
        // Carla manages sales and nothing of engineering: she may not pull someone out of another department into her own.
        var carla = Guid.NewGuid();
        _store.Add(FgaTuple.UserId(carla), "manager", FgaTuple.DepartmentId(_sales));
        var pulledIn = await Transfer(carla, employee, _sales);
        // Within the tree they both manage, it goes through.
        var withinBackend = await Transfer(_boris, employee, _backend);

        Assert.Equal("employee.department.not_manager", intoSales.Error.Messages[0].Code);
        Assert.Equal("employee.department.not_manager", annaIntoSales.Error.Messages[0].Code);
        Assert.Equal("employee.department.not_manager", pulledIn.Error.Messages[0].Code);
        Assert.True(withinBackend.IsSuccess);
    }

    // ---- the one thing a client could try ----

    [Fact]
    public async Task A_request_body_that_says_the_caller_is_an_administrator_changes_nothing()
    {
        await GrowTree();
        await using var scope = _services.CreateAsyncScope();
        var controller = new EmployeeController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("sub", _boris.ToString()), new Claim(ClaimTypes.Role, "editor")], "test")),
                },
            },
        };

        // What a client would send: the whole command is bound from the body, including the field that says they are an administrator.
        var forged = new HireEmployeeCommand("Forged Admin", $"forged-{Guid.NewGuid():N}@test.local", _sales, Guid.NewGuid(), CallerIsAdmin: true);
        var result = await controller.Hire(scope.ServiceProvider.GetRequiredService<HireEmployeeHandler>(), forged, null, CancellationToken.None);

        // The endpoint's own answer is what a client sees: executed against a response, it is a refusal of the caller.
        var response = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        response.Response.Body = new MemoryStream();
        await result.ExecuteAsync(response);
        response.Response.Body.Position = 0;
        var body = await new StreamReader(response.Response.Body).ReadToEndAsync();

        Assert.Equal(StatusCodes.Status403Forbidden, response.Response.StatusCode);
        Assert.Contains("employee.department.not_manager", body);
        Assert.Equal(0, await CountEmployees());
    }

    // ---- helpers ----

    private Task<AccessDecision> Decide(Guid? account, Guid department, bool isAdmin = false) =>
        new TreeDepartmentAuthorization(_store, NullLogger<TreeDepartmentAuthorization>.Instance)
            .CanManageAsync(new Caller(account, isAdmin), department, CancellationToken.None);

    private async Task Sync(Func<DepartmentTreeSync, Task> act)
    {
        await using var scope = _services.CreateAsyncScope();
        await act(scope.ServiceProvider.GetRequiredService<DepartmentTreeSync>());
    }

    private async Task<CSharpFunctionalExtensions.Result<Guid, Error>> Hire(Guid by, Guid department, bool isAdmin = false)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HireEmployeeHandler>().Handle(
            new HireEmployeeCommand($"Person {Guid.NewGuid():N}", $"p-{Guid.NewGuid():N}@test.local", department, Guid.NewGuid(), by, CallerIsAdmin: isAdmin),
            CancellationToken.None);
    }

    private async Task<CSharpFunctionalExtensions.UnitResult<Error>> Transfer(Guid by, Guid employee, Guid department)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TransferEmployeeHandler>().Handle(
            new TransferEmployeeCommand(employee, department, Guid.NewGuid(), by, false), CancellationToken.None);
    }

    // A new hire waits for its account and cannot be transferred until it has one.
    private async Task Activate(Guid employee)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EmployeeDbContext>();
        (await db.Set<Employee>().SingleAsync(e => e.Id == employee)).CompleteProvisioning();
        await db.SaveChangesAsync();
    }

    private async Task<int> CountEmployees()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EmployeeDbContext>().Set<Employee>().CountAsync();
    }

    // The store as far as these tests need it: tuples, and the one relation the model defines (managing a department, or any above it).
    private sealed class FakeFga : IFgaClient
    {
        private readonly object gate = new();

        public HashSet<FgaTuple> Tuples { get; } = [];

        public bool Down { get; set; }

        public int Checks { get; private set; }

        public void Add(string user, string relation, string obj)
        {
            lock (gate)
            {
                Tuples.Add(new FgaTuple(user, relation, obj));
            }
        }

        public Task<bool> CheckAsync(FgaTuple tuple, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                Checks++;
                if (Down)
                {
                    throw new FgaException("the store is down");
                }

                return Task.FromResult(Manages(tuple.User, tuple.Object, 0));
            }
        }

        public Task WriteAsync(IReadOnlyCollection<FgaTuple> writes, IReadOnlyCollection<FgaTuple> deletes, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (Down)
                {
                    throw new FgaException("the store is down");
                }

                foreach (var d in deletes)
                {
                    Tuples.Remove(d);
                }

                foreach (var w in writes)
                {
                    Tuples.Add(w);
                }
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FgaTuple>> ReadAsync(string obj, string? relation, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (Down)
                {
                    throw new FgaException("the store is down");
                }

                return Task.FromResult<IReadOnlyList<FgaTuple>>(
                    Tuples.Where(t => t.Object == obj && (relation is null || t.Relation == relation)).ToList());
            }
        }

        private bool Manages(string user, string department, int depth) =>
            depth < 25
            && (Tuples.Contains(new FgaTuple(user, "manager", department))
                || Tuples.Any(t => t.Relation == "parent" && t.Object == department && Manages(user, t.User, depth + 1)));
    }
}
