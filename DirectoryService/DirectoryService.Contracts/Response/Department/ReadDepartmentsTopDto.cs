namespace DirectoryService.Contracts.Response.Department;

public record ReadDepartmentsTopDto
{
    public Guid Id { get; init; }

    public Guid? ParentId { get; init; }

    public string Name { get; init; } = null!;

    public string Identifier { get; init; } = null!;

    public string Path { get; init; } = null!;

    public int Depth { get; init; }

    public bool IsActive { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    public int PositionCount { get; init; }
}