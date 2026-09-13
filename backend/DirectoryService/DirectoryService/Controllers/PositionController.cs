using DirectoryService.Application.Position;
using DirectoryService.Application.Position.Queries;
using DirectoryService.Contracts.Request.Position;
using DirectoryService.Contracts.Response.Position;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared;
using Shared.EndpointResults;

namespace DirectoryService.Controllers;

[ApiController]
[Route("api/positions")]
[Authorize]
public class PositionController : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = "CanEdit")]
    public async Task<EndpointResult<Guid>> Create(
        [FromServices] CreatePositionHandle handler,
        CreatePositionCommand request, CancellationToken cancellationToken) => await handler.Handle(request, cancellationToken);

    /// <summary>
    /// Каталог позиций с привязанными департаментами. Сортировка: активные первыми,
    /// затем по имени. Размер страницы ограничен <see cref="PagedResponse{T}.MaxSize"/>.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [HttpGet]
    [ProducesResponseType<PagedResponse<ReadPositionDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<ReadPositionDto>>> GetPositions(
        [FromQuery] GetPositionsRequest request,
        [FromServices] GetPositionsHandler handler,
        CancellationToken cancellationToken) =>
        await handler.Handle(request, cancellationToken);
}