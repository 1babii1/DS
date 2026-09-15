namespace DirectoryService.Contracts.Request.Position;

/// <param name="DepartmentId">Необязательный фильтр: только позиции этого департамента.</param>
/// <param name="Search">Поиск по имени, без учёта регистра.</param>
/// <param name="IsActive">Необязательный фильтр по активности; по умолчанию возвращаются все.</param>
public record GetPositionsRequest(
    Guid? DepartmentId = null,
    string? Search = null,
    bool? IsActive = null,
    int? Page = null,
    int? Size = null);