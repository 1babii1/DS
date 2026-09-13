using System.ComponentModel;
using Dapper;
using McpServer.Embeddings;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Npgsql;

namespace McpServer.Tools;

[McpServerToolType]
public sealed class DirectoryTools(
    NpgsqlDataSource dataSource,
    OllamaEmbeddingClient embeddingClient,
    IOptions<EmbeddingsOptions> embeddingOptions)
{
    [McpServerTool(Name = "search_departments")]
    [Description("Semantic search for departments by meaning (e.g. \"teams working on payments\"), not exact text match. Returns id, name, identifier and a similarity score (0-1, higher is closer).")]
    public async Task<IReadOnlyList<DepartmentSearchResult>> SearchDepartments(
        [Description("Free-text description of what you're looking for")] string query,
        [Description("Max results to return (1-50)")] int limit = 10)
    {
        limit = Math.Clamp(limit, 1, 50);
        var vector = await embeddingClient.EmbedAsync(embeddingOptions.Value.Model, query, CancellationToken.None);

        await using var connection = dataSource.CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select d.id, d.name, d.identifier, 1 - (e.embedding <=> $1) as score
            from directory.department_embeddings e
            join directory.departments d on d.id = e.department_id
            where d.is_active
            order by e.embedding <=> $1
            limit $2
            """;
        command.Parameters.Add(new NpgsqlParameter { Value = vector });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });

        var results = new List<DepartmentSearchResult>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DepartmentSearchResult(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDouble(3)));
        }

        return results;
    }

    [McpServerTool(Name = "get_department_tree")]
    [Description("Returns a department and all of its descendants using the org structure hierarchy. Pass no id to list top-level (root) departments only.")]
    public async Task<IReadOnlyList<DepartmentNode>> GetDepartmentTree(
        [Description("Department id to root the subtree at; omit for the top-level departments")] Guid? departmentId = null)
    {
        await using var connection = dataSource.CreateConnection();

        if (departmentId is null)
        {
            const string rootsSql = """
                select id, name, identifier, depth, parent_id
                from directory.departments
                where is_active and parent_id is null
                order by name
                """;
            var roots = await connection.QueryAsync<DepartmentNode>(rootsSql);
            return roots.ToList();
        }

        const string subtreeSql = """
            select d.id, d.name, d.identifier, d.depth, d.parent_id
            from directory.departments d
            where d.is_active
              and d.path <@ (select path from directory.departments where id = @departmentId)
            order by d.depth, d.name
            """;
        var subtree = await connection.QueryAsync<DepartmentNode>(subtreeSql, new { departmentId });
        return subtree.ToList();
    }

    [McpServerTool(Name = "get_employee")]
    [Description("Looks up a single employee by id.")]
    public async Task<EmployeeDetails?> GetEmployee(
        [Description("Employee id")] Guid employeeId)
    {
        await using var connection = dataSource.CreateConnection();
        const string sql = """
            select "Id" as Id, "FullName" as FullName, "Email" as Email,
                   "DepartmentId" as DepartmentId, "DepartmentName" as DepartmentName,
                   "PositionId" as PositionId, "PositionName" as PositionName,
                   "Status" as Status, "HiredAt" as HiredAt
            from employee.employees
            where "Id" = @employeeId
            """;
        return await connection.QuerySingleOrDefaultAsync<EmployeeDetails>(sql, new { employeeId });
    }

    [McpServerTool(Name = "list_employees_by_department")]
    [Description("Lists employees currently assigned to a department.")]
    public async Task<IReadOnlyList<EmployeeDetails>> ListEmployeesByDepartment(
        [Description("Department id")] Guid departmentId)
    {
        await using var connection = dataSource.CreateConnection();
        const string sql = """
            select "Id" as Id, "FullName" as FullName, "Email" as Email,
                   "DepartmentId" as DepartmentId, "DepartmentName" as DepartmentName,
                   "PositionId" as PositionId, "PositionName" as PositionName,
                   "Status" as Status, "HiredAt" as HiredAt
            from employee.employees
            where "DepartmentId" = @departmentId
            order by "FullName"
            """;
        var employees = await connection.QueryAsync<EmployeeDetails>(sql, new { departmentId });
        return employees.ToList();
    }
}

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
