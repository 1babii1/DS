namespace DirectoryService.Contracts.Request.Location;

public record GetLocationWithFiltersRequest(string Search, int? Size = 10, int? Page = 1, string? SortBy = "created_at");