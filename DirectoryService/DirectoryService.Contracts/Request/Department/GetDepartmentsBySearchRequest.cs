namespace DirectoryService.Contracts.Request.Department;

public record GetDepartmentsBySearchRequest(string Search, int? Size = 10, int? Page = 1);