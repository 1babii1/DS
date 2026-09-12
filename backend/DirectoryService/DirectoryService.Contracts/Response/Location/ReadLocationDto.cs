namespace DirectoryService.Contracts.Response.Location;

public record ReadLocationDto
{
    public Guid Id { get; init; }

    public string Name { get; init; } = null!;

    public string Timezone { get; init; } = null!;

    public string Street { get; init; } = null!;

    public string City { get; init; } = null!;

    public string Country { get; init; } = null!;

    public bool IsActive { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }
}