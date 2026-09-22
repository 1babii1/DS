using DirectoryService.Application.Department.Commands;
using DirectoryService.Application.Department.Queries;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Contracts.Response.Department;
using DirectoryService.Domain.Departments.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Shared.EndpointResults;
using Shared.Security;

namespace DirectoryService.Controllers;

[ApiController]
[Route("api/departments")]
[Authorize]
public class DepartmentController : ControllerBase
{
    [HttpPost]
    [RequireCanEdit]
    [EnableRateLimiting("write")]
    public async Task<EndpointResult<Guid>> Create(
        [FromServices] CreateDepartmentHandler handler,
        CreateDepartmentCommand request, CancellationToken cancellationToken) =>
        await handler.Handle(request, cancellationToken);

    [HttpPatch("locations")]
    [RequireCanEdit]
    [EnableRateLimiting("write")]
    public async Task<EndpointResult<DepartmentId>> UpdateLocations(
        [FromServices] UpdateDepartmentLocationsHandler handler,
        UpdateDepartmentLocationsCommand request, CancellationToken cancellationToken) =>
        await handler.Handle(request, cancellationToken);

    [HttpPut("{departmentId:guid}/parent")]
    [RequireCanEdit]
    [EnableRateLimiting("write")]
    public async Task<EndpointResult<DepartmentId>> UpdateParent(
        [FromRoute] Guid departmentId,
        [FromServices] UpdateParentDepartmentHandler handler,
        UpdateParentDepartmentRequest request, CancellationToken cancellationToken)
    {
        var command = new UpdateParentDepartmentCommand(departmentId, request);
        return await handler.Handle(command, cancellationToken);
    }

    [HttpGet("department/{departmentId:guid}")]
    public async Task<EndpointResult<ReadDepartmentWithChildrenDto?>> GetDepartmentById(
        [FromRoute] Guid departmentId,
        [FromServices] GetDepartmentByIdHandler handler,
        CancellationToken cancellationToken) =>
        await handler.Handle(new GetDepartmentByIdRequest(departmentId), cancellationToken);

    [HttpGet("department/location")]
    public async Task<ActionResult<List<ReadDepartmentDto>?>> GetDepartmentByLocation(
        [FromQuery] GetDepartmentByLocationRequest request,
        [FromServices] GetDepartmentByLocationHandler handler,
        CancellationToken cancellationToken) =>
        await handler.Handle(request, cancellationToken);

    [HttpGet("search")]
    [EnableRateLimiting("search")]
    public async Task<EndpointResult<List<DepartmentSearchResultDto>>> SearchSemantic(
        [FromQuery] string query,
        [FromQuery] int limit,
        [FromServices] SearchDepartmentsSemanticHandler handler,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = limit <= 0 ? 10 : limit;
        var request = new SearchDepartmentsSemanticRequest(query, effectiveLimit);
        return await handler.Handle(request, cancellationToken);
    }

    [HttpGet("top-positions")]
    public async Task<ActionResult<List<ReadDepartmentsTopDto>?>> GetDepartmentsTopForPositions(
        [FromServices] GetDepartmentsTopByPositionsHandler handler,
        CancellationToken cancellationToken) =>
        await handler.Handle(cancellationToken);

    [HttpGet("roots")]
    public async Task<EndpointResult<List<ReadDepartmentHierarchyDto>>> GetRootDepartments(
        [FromQuery] GetParentDepartmentsRequest request,
        [FromServices] GetParentDepartmentsHandler handler,
        CancellationToken cancellationToken) =>
        await handler.Handle(request, cancellationToken);

    [HttpGet("{parentId:guid}/children")]
    public async Task<EndpointResult<List<ReadDepartmentHierarchyDto>>> GetChildrenLazy(
        [FromRoute] Guid parentId,
        [FromQuery] GetChildrenLazyRequest request,
        [FromServices] GetChildrenLazyHandler handler,
        CancellationToken cancellationToken)
    {
        var command = new GetChildrenLazyCommand(parentId, request);
        return await handler.Handle(command, cancellationToken);
    }

    [HttpDelete("{departmentId:guid}")]
    [RequireCanEdit]
    [EnableRateLimiting("write")]
    public async Task<EndpointResult<DepartmentId>> SoftDeleteDepartments(
        [FromRoute] SoftDeleteDepartmentRequest request,
        [FromServices] SoftDeleteDepartmentHandler handler,
        CancellationToken cancellationToken) => await handler.Handle(request, cancellationToken);
}