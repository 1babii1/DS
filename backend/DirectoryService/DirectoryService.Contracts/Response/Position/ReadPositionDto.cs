namespace DirectoryService.Contracts.Response.Position;

public record ReadPositionDto
{
    public Guid Id { get; init; }

    public string Name { get; init; } = null!;

    public string? Description { get; init; }

    public bool IsActive { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    /// <summary>Департаменты, к которым привязана позиция - без них клиент не может
    /// построить селект "должность в подразделении" и вынужден догадываться.</summary>
    public IReadOnlyList<PositionDepartmentDto> Departments { get; init; } = [];
}

public record PositionDepartmentDto(Guid Id, string Name, string Identifier);