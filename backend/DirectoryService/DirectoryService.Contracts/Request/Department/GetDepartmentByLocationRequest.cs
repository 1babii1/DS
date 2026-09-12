namespace DirectoryService.Contracts.Request.Department;

public record GetDepartmentByLocationRequest
{
    public Guid[]? LocationIds { get; set; }

    public string? Search { get; set; }

    public bool? IsActive { get; set; }

    public int? Page { get; set; } = 1;

    public int? PageSize { get; set; } = 20;
}