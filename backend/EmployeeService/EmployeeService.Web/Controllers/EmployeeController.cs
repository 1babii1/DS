using EmployeeService.Application.Employees.Commands;
using EmployeeService.Application.Employees.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared;
using Shared.EndpointResults;

namespace EmployeeService.Web.Controllers;

[ApiController]
[Route("api/employees")]
[Authorize]
public class EmployeeController : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = "CanEdit")]
    public async Task<EndpointResult<Guid>> Hire(
        [FromServices] HireEmployeeHandler handler,
        HireEmployeeCommand command,
        CancellationToken cancellationToken) =>
        await handler.Handle(command, cancellationToken);

    [HttpPut("{employeeId:guid}/transfer")]
    [Authorize(Policy = "CanEdit")]
    public async Task<EndpointResult> Transfer(
        [FromRoute] Guid employeeId,
        [FromServices] TransferEmployeeHandler handler,
        [FromBody] TransferEmployeeRequest request,
        CancellationToken cancellationToken)
    {
        var command = new TransferEmployeeCommand(employeeId, request.DepartmentId, request.PositionId);
        return await handler.Handle(command, cancellationToken);
    }

    [HttpGet("{employeeId:guid}")]
    public async Task<ActionResult<EmployeeDto>> GetById(
        [FromRoute] Guid employeeId,
        [FromServices] GetEmployeeByIdHandler handler,
        CancellationToken cancellationToken)
    {
        var employee = await handler.Handle(employeeId, cancellationToken);
        return employee is null ? NotFound() : Ok(employee);
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<EmployeeDto>>> List(
        [FromQuery] Guid? departmentId,
        [FromQuery] int? page,
        [FromQuery] int? size,
        [FromServices] ListEmployeesHandler handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.Handle(departmentId, page, size, cancellationToken));
}

public record TransferEmployeeRequest(Guid DepartmentId, Guid PositionId);