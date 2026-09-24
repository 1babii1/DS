using CSharpFunctionalExtensions;
using Dapper;
using DirectoryService.Application.Database;
using DirectoryService.Contracts.Response.Department;
using Shared;

namespace DirectoryService.Application.Department.Queries;

// A department and everything beneath it in one bounded query, owned here rather than rebuilt by
// each caller. Callers that walked the tree themselves either issued one request per node or, in
// McpServer's case, read this service's tables directly; both put the shape of the hierarchy in
// someone else's hands.
public class GetSubtreeHandler(IDbConnectionFactory connectionFactory, int maxNodes = GetSubtreeHandler.MaxNodes)
{
    // The default; the constructor takes it as a parameter so the cut-off can be tested without
    // creating hundreds of departments. A subtree larger than this is cut and flagged, never returned whole: an unbounded read of the
    // org tree is exactly what the paged endpoints exist to prevent.
    public const int MaxNodes = 500;

    public async Task<Result<DepartmentSubtreeDto, Error>> Handle(Guid departmentId, CancellationToken cancellationToken)
    {
        if (departmentId == Guid.Empty)
        {
            return Error.Validation("department.id.empty", "departmentId cant be null", "departmentId");
        }

        using var connection = await connectionFactory.CreateConnectionAsync(cancellationToken);

        // One extra row tells "exactly MaxNodes" from "more than MaxNodes". An inactive (deleted)
        // department has no browsable tree, and inactive descendants are not part of it.
        var rows = (await connection.QueryAsync<ReadDepartmentHierarchyDto>(
            new CommandDefinition(
                """
                SELECT d.id, d.name, d.parent_id, d.created_at, d.updated_at, d.is_active,
                       d.identifier, d.path, d.depth
                FROM departments d
                WHERE d.is_active
                  AND d.path <@ (SELECT path FROM departments WHERE id = @departmentId AND is_active)
                ORDER BY d.depth, d.name
                LIMIT @limit
                """,
                new { departmentId, limit = maxNodes + 1 },
                cancellationToken: cancellationToken))).ToList();

        var truncated = rows.Count > maxNodes;
        return new DepartmentSubtreeDto(truncated ? rows[..maxNodes] : rows, truncated);
    }
}
