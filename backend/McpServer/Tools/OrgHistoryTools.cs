using System.ComponentModel;
using System.Globalization;
using McpServer.Api;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

// Answers "how did the organization look then, and who worked where" from AuditService's recorded history. Read only,
// as the caller. Both shapes are bounded: the whole-org answer lists departments without their people, and a single
// department lists its people up to a cap, so one call cannot return an unbounded slice of the directory.
[McpServerToolType]
public sealed class OrgHistoryTools(AuditApiClient audit)
{
    public const int MaxDepartments = 200;
    public const int MaxPeople = 100;

    [McpServerTool(Name = "get_org_snapshot")]
    [Description("Shows the organization as it was on a past date, rebuilt from the recorded history: departments with the names and parents they had then, and who worked in a department in which position. Use it for questions like \"who worked in Payments in March?\" or \"what did the org look like on 1 June?\". Pass a date (2026-03-05, meaning the end of that day, UTC) or a time (2026-03-05T10:00:00Z). Without departmentId it lists the departments of that day (name, parent, depth, headcount, no people); with a departmentId it lists that department's people. Department ids are the same as today's; names and people are as of the date. History covers only what was recorded, so an early date can be empty.")]
    public Task<OrgSnapshotResult> GetOrgSnapshot(
        [Description("A date like 2026-03-05, or a time like 2026-03-05T10:00:00Z")] string at,
        [Description("Optional: a department id, to list who worked in that department on that date")] Guid? departmentId = null,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            RequireDate(at);
            var snapshot = await audit.OrgChartAsync(at.Trim(), cancellationToken);

            if (departmentId is not { } id)
            {
                var all = snapshot.Departments;
                return new OrgSnapshotResult(
                    snapshot.At,
                    Departments: all.Take(MaxDepartments)
                        .Select(d => new OrgSnapshotDepartmentSummary(d.Id, d.Name, d.ParentId, d.Depth, d.People.Count))
                        .ToList(),
                    DepartmentsTruncated: all.Count > MaxDepartments,
                    Department: null,
                    Unplaced: snapshot.Unplaced.Count,
                    Note: all.Count == 0 ? "Nothing had been recorded by that date." : null);
            }

            var department = snapshot.Departments.FirstOrDefault(d => d.Id == id);
            return department is null
                ? new OrgSnapshotResult(snapshot.At, null, false, null, snapshot.Unplaced.Count, "No department with that id existed on that date.")
                : new OrgSnapshotResult(
                    snapshot.At,
                    Departments: null,
                    DepartmentsTruncated: false,
                    Department: new OrgSnapshotDepartmentDetail(
                        department.Id,
                        department.Name,
                        department.ParentId,
                        department.Depth,
                        department.People.Count,
                        department.People.Take(MaxPeople).ToList(),
                        department.People.Count > MaxPeople),
                    Unplaced: snapshot.Unplaced.Count,
                    Note: null);
        });

    // The service is the authority on the date, but a model that passes "last March" should be told what is expected
    // without a round trip, and nothing is sent for a value that cannot be a date.
    private static void RequireDate(string at)
    {
        if (string.IsNullOrWhiteSpace(at)
            || !DateTime.TryParse(
                at.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed)
            || parsed.Year < 2000)
        {
            throw new McpException("at must be a date like 2026-03-05, or a time like 2026-03-05T10:00:00Z.");
        }
    }

    // Only the fixed, category-level message of a failed call reaches the model (same rule as DirectoryTools).
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

public sealed record OrgSnapshotResult(
    DateTime At,
    IReadOnlyList<OrgSnapshotDepartmentSummary>? Departments,
    bool DepartmentsTruncated,
    OrgSnapshotDepartmentDetail? Department,
    int Unplaced,
    string? Note);

public sealed record OrgSnapshotDepartmentSummary(Guid Id, string Name, Guid? ParentId, int Depth, int Headcount);

public sealed record OrgSnapshotDepartmentDetail(
    Guid Id, string Name, Guid? ParentId, int Depth, int Headcount, IReadOnlyList<OrgSnapshotPerson> People, bool PeopleTruncated);
