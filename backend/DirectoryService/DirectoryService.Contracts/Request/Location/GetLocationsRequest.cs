namespace DirectoryService.Contracts.Request.Location;

/// <param name="DepartmentId">Необязательный фильтр: только локации этого департамента.
/// Без него возвращается весь каталог, включая локации без привязок.</param>
/// <param name="Search">Поиск по имени, без учёта регистра.</param>
/// <param name="IsActive">Необязательный фильтр по активности; по умолчанию возвращаются все.</param>
public record GetLocationsRequest(
    Guid? DepartmentId = null,
    string? Search = null,
    bool? IsActive = null,
    int? Page = null,
    int? Size = null);