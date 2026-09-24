using System.ComponentModel;
using McpServer.Api;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Shared;

namespace McpServer.Tools;

// Every tool reads through the owning service's own API with the caller's token (see
// BearerForwardingHandler) - not from that service's database. What each service allows,
// filters and pages is decided in one place, its own, and applies here without a second copy.
[McpServerToolType]
public sealed class DirectoryTools(DirectoryApiClient directory, EmployeeApiClient employees)
{
    [McpServerTool(Name = "search_departments")]
    [Description("Semantic search for departments by meaning (e.g. \"teams working on payments\"), not exact text match. Returns id, name, identifier and a similarity score (0-1, higher is closer).")]
    public Task<IReadOnlyList<DepartmentSearchResult>> SearchDepartments(
        [Description("Free-text description of what you're looking for")] string query,
        [Description("Max results to return (1-50)")] int limit = 10,
        CancellationToken cancellationToken = default) =>
        Run(() => directory.SearchAsync(query, Math.Clamp(limit, 1, 50), cancellationToken));

    [McpServerTool(Name = "get_department_tree")]
    [Description("Returns a department and all of its active descendants, shallowest first (hasMore is true if the subtree was too large and got cut). Pass no id to list top-level (root) departments instead, one page at a time: hasMore then means another page probably exists.")]
    public Task<DepartmentTreeResult> GetDepartmentTree(
        [Description("Department id to root the subtree at; omit for the top-level departments")] Guid? departmentId = null,
        [Description("Page of top-level departments (only used when no id is given)")] int page = 1,
        CancellationToken cancellationToken = default) =>
        Run(() => departmentId is null
            ? directory.RootsAsync(ClampPage(page), cancellationToken)
            : directory.SubtreeAsync(departmentId.Value, cancellationToken));

    [McpServerTool(Name = "get_employee")]
    [Description("Looks up a single employee by id.")]
    public Task<EmployeeDetails?> GetEmployee(
        [Description("Employee id")] Guid employeeId,
        CancellationToken cancellationToken = default) =>
        Run(() => employees.GetAsync(employeeId, cancellationToken));

    [McpServerTool(Name = "list_employees_by_department")]
    [Description("Lists employees currently assigned to a department, one page at a time. Check hasNext and ask for the next page for more.")]
    public Task<PagedResponse<EmployeeDetails>> ListEmployeesByDepartment(
        [Description("Department id")] Guid departmentId,
        [Description("Page number, starting at 1")] int page = 1,
        [Description("Page size (the service caps this)")] int size = PagedResponse<EmployeeDetails>.DefaultSize,
        CancellationToken cancellationToken = default) =>
        Run(() => employees.ListByDepartmentAsync(
            departmentId, ClampPage(page), Math.Clamp(size, 1, PagedResponse<EmployeeDetails>.MaxSize), cancellationToken));

    // Far beyond any real page count, but small enough that the services' (page - 1) * size cannot overflow.
    private static int ClampPage(int page) => Math.Clamp(page, 1, 100_000);

    // Only the fixed, category-level message of a failed call reaches the model. McpException is
    // the type whose message the MCP SDK passes through; anything else is replaced by a generic one.
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
            // Unreachable, timed out, or the breaker is open: one fixed sentence for all three, not the
            // SDK's generic error path.
            throw new McpException("The service could not be reached.", ex);
        }
    }
}

/// <param name="HasMore">There is more than was returned (a next page, or a subtree that was cut).</param>
public sealed record DepartmentTreeResult(IReadOnlyList<DepartmentNode> Nodes, bool HasMore);

public sealed record DepartmentSearchResult(Guid Id, string Name, string Identifier, double Score);

public sealed record DepartmentNode(Guid Id, string Name, string Identifier, short Depth, Guid? ParentId);

public sealed record EmployeeDetails(
    Guid Id,
    string FullName,
    string Email,
    Guid DepartmentId,
    string DepartmentName,
    Guid PositionId,
    string PositionName,
    string Status,
    DateTime HiredAt);
