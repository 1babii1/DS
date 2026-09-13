using DirectoryService.Application.Location.Commands;
using DirectoryService.Application.Location.Queries;
using DirectoryService.Contracts.Request.Location;
using DirectoryService.Contracts.Response.Location;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared;
using Shared.EndpointResults;

namespace DirectoryService.Controllers;

[ApiController]
[Route("api/locations")]
[Authorize]
public class LocationController : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = "CanEdit")]
    public async Task<EndpointResult<Guid>> Create(
        [FromServices] CreateLocationHandle handler,
        CreateLocationCommand request, CancellationToken cancellationToken) =>
        await handler.Handle(request, cancellationToken);

    /// <summary>
    /// Каталог локаций. Без departmentId возвращает все локации, включая ещё не
    /// привязанные ни к одному департаменту; с departmentId фильтрует по привязке.
    /// Сортировка: активные первыми, затем по имени. Размер страницы ограничен
    /// <see cref="PagedResponse{T}.MaxSize"/>.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    [HttpGet]
    [ProducesResponseType<PagedResponse<ReadLocationDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<ReadLocationDto>>> GetLocations(
        [FromQuery] GetLocationsRequest request,
        [FromServices] GetLocationsHandler handler,
        CancellationToken cancellationToken) =>
        await handler.Handle(request, cancellationToken);

    /// <summary>
    /// Локации конкретного департамента. Оставлен для существующих потребителей;
    /// для каталога используйте GET /api/locations.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    [HttpGet("by-department")]
    public async Task<ActionResult<List<ReadLocationDto>?>> GetLocationByDepartment(
        [FromQuery] GetLocationByDepartmentRequest request,
        [FromServices] GetLocationByDepartmentHandle handler,
        CancellationToken cancellationToken) => await handler.Handle(request, cancellationToken);
}