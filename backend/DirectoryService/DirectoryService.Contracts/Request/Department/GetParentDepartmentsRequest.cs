namespace DirectoryService.Contracts.Request.Department;

public record GetParentDepartmentsRequest(int? Page = 1, int? Size = 20, int? Preferch = 3);