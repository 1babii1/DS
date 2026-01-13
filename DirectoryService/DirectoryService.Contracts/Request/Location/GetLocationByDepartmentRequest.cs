namespace DirectoryService.Contracts.Request.Location;

public record GetLocationByDepartmentRequest
{
    public Guid[]? DepartmentId { get; set; }

    public string? Search { get; set; }

    public bool? IsActive { get; set; }

    public int? Page { get; set; } = 1;

    public int? PageSize { get; set; } = 20;
}