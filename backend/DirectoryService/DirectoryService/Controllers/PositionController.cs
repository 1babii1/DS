using DirectoryService.Application.Position;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
}