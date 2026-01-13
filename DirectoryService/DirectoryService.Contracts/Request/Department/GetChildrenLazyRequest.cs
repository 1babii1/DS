using Microsoft.AspNetCore.Mvc;

namespace DirectoryService.Contracts.Request.Department;

public record GetChildrenLazyRequest(int? Page = 1, int? PageSize = 20);