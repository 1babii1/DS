using DirectoryService.Application.Location.Commands;
using DirectoryService.Application.Location.Queries;
using DirectoryService.Contracts.Request.Location;
using DirectoryService.Contracts.Response.Location;
using Microsoft.AspNetCore.Mvc;
using Shared.EndpointResults;

namespace DirectoryService.Controllers;

[ApiController]
[Route("api/locations")]
public class LocationController : ControllerBase
{
    [HttpPost]
    public async Task<EndpointResult<Guid>> Create(
        [FromServices] CreateLocationHandle handler,
        CreateLocationCommand request, CancellationToken cancellationToken) =>
        await handler.Handle(request, cancellationToken);

    [HttpGet]
    public async Task<ActionResult<List<ReadLocationDto>?>> GetLocationById(
        [FromQuery] GetLocationByDepartmentRequest request,
        [FromServices] GetLocationByDepartmentHandle handler,
        CancellationToken cancellationToken) => await handler.Handle(request, cancellationToken);
}