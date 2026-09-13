using EmployeeService.Application.Employees.Commands;
using EmployeeService.Application.Employees.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared;

namespace EmployeeService.Web.Controllers;

[ApiController]
[Route("api/employees")]
[Authorize]
public class EmployeeController : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = "CanEdit")]
    public async Task<IActionResult> Hire(
        [FromServices] HireEmployeeHandler handler,
        HireEmployeeCommand command,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(command, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ToProblem(result.Error);
    }

    [HttpPut("{employeeId:guid}/transfer")]
    [Authorize(Policy = "CanEdit")]
    public async Task<IActionResult> Transfer(
        [FromRoute] Guid employeeId,
        [FromServices] TransferEmployeeHandler handler,
        [FromBody] TransferEmployeeRequest request,
        CancellationToken cancellationToken)
    {
        var command = new TransferEmployeeCommand(employeeId, request.DepartmentId, request.PositionId);
        var result = await handler.Handle(command, cancellationToken);
        return result.IsSuccess ? NoContent() : ToProblem(result.Error);
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
    public async Task<ActionResult<List<EmployeeDto>>> List(
        [FromQuery] Guid? departmentId,
        [FromServices] ListEmployeesHandler handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.Handle(departmentId, cancellationToken));

    private IActionResult ToProblem(Error error) => error.Type switch
    {
        ErrorType.NOT_FOUND => NotFound(error.Messages),
        ErrorType.VALIDATION => BadRequest(error.Messages),
        ErrorType.CONFLICT => Conflict(error.Messages),
        _ => Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: error.Messages.FirstOrDefault()?.Message),
    };
}

public record TransferEmployeeRequest(Guid DepartmentId, Guid PositionId);
