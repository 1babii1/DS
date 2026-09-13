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
    public async Task<ActionResult<PagedResponse<EmployeeDto>>> List(
        [FromQuery] Guid? departmentId,
        [FromQuery] int? page,
        [FromQuery] int? size,
        [FromServices] ListEmployeesHandler handler,
        CancellationToken cancellationToken) =>
        Ok(await handler.Handle(departmentId, page, size, cancellationToken));

    // 503 отдаётся только при реальной недоступности зависимости. Раньше сюда попадала
    // любая неклассифицированная ошибка, включая отказ авторизации, из-за чего клиент
    // видел "сервис недоступен" там, где стоило чинить права.
    private IActionResult ToProblem(Error error)
    {
        if (error.Messages.Any(m => m.Code == "employee.directory.unavailable"))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Envelope.Fail(error));
        }

        var status = error.Type switch
        {
            ErrorType.NOT_FOUND => StatusCodes.Status404NotFound,
            ErrorType.VALIDATION => StatusCodes.Status400BadRequest,
            ErrorType.CONFLICT => StatusCodes.Status409Conflict,
            ErrorType.AUTHENTICATION => StatusCodes.Status401Unauthorized,
            ErrorType.AUTHORIZATION => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status500InternalServerError,
        };

        return StatusCode(status, Envelope.Fail(error));
    }
}

public record TransferEmployeeRequest(Guid DepartmentId, Guid PositionId);
