using McpServer.Api;
using McpServer.Tools;
using ModelContextProtocol;

namespace McpServer.Agent;

// Turns the ids a model hands over into things that exist, read from the owning services as the caller. A plan is
// built only from what comes back, so the person approving sees names the services gave, and an id that points at
// nothing (or at the wrong kind of thing) never becomes a plan. Reads only: this holds no command client.
public sealed class PlanLookup(DirectoryApiClient directory, EmployeeApiClient employees)
{
    private const int MaxName = 200;

    public Task<EmployeeDetails> EmployeeAsync(Guid employeeId, CancellationToken ct) => Run(async () =>
    {
        var employee = await employees.GetAsync(employeeId, ct)
            ?? throw new McpException("There is no employee with that id. Look the employee up first and use the id you find.");
        if (employee.Status is "Terminated" or "ProvisioningFailed")
        {
            throw new McpException("That employee is no longer with the organization, so nothing can be proposed for them.");
        }

        Shown(employee.FullName);
        Shown(employee.DepartmentName);
        Shown(employee.PositionName);
        return employee;
    });

    public Task<string> DepartmentNameAsync(Guid departmentId, CancellationToken ct) => Run(async () =>
    {
        var department = await directory.GetDepartmentAsync(departmentId, ct);
        if (department is not { IsActive: true })
        {
            throw new McpException(
                "There is no active department with that id. Search for the department and use the id you find; " +
                "a position id or an employee id is not a department id.");
        }

        return Shown(department.Name);
    });

    // The position must be one this department has. That is stricter than "exists" on purpose: it is also what
    // catches an id of the wrong kind.
    public Task<string> PositionNameAsync(Guid departmentId, Guid positionId, CancellationToken ct) => Run(async () =>
    {
        var positions = await directory.ActivePositionsOfAsync(departmentId, ct);
        var position = positions.FirstOrDefault(p => p.Id == positionId)
            ?? throw new McpException("That position does not exist in that department, or is not active.");
        return Shown(position.Name);
    });

    // Names come from data other people wrote. They are shown quoted on one line, so instruction-like text stays a
    // value; text that could hide or reshape what is displayed is refused instead of shown.
    private static string Shown(string name) =>
        Text.IsClean(name, MaxName)
            ? name
            : throw new McpException("A name on record contains characters that cannot be shown safely, so no plan was made.");

    private static async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ServiceApiException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException
            or Polly.CircuitBreaker.BrokenCircuitException
            or Polly.Timeout.TimeoutRejectedException)
        {
            throw new McpException("The service could not be reached.", ex);
        }
    }
}
