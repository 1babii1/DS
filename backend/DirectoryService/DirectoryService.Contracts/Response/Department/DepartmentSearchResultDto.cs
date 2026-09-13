namespace DirectoryService.Contracts.Response.Department;

public record DepartmentSearchResultDto
{
    public Guid Id { get; init; }

    public string Name { get; init; } = null!;

    public string Identifier { get; init; } = null!;

    public double Score { get; init; }
}