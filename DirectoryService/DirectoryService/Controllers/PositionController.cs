using DirectoryService.Application.Position;
using Microsoft.AspNetCore.Mvc;
using Shared.EndpointResults;

namespace DirectoryService.Controllers;

[ApiController]
[Route("api/positions")]
public class PositionController : ControllerBase
{
    [HttpPost]
    public async Task<EndpointResult<Guid>> Create(
        [FromServices] CreatePositionHandle handler,
        CreatePositionCommand request, CancellationToken cancellationToken) => await handler.Handle(request, cancellationToken);
}