using System.Security.Claims;
using EmployeeService.Application.Employees.Commands;
using EmployeeService.Application.Employees.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Shared;
using Shared.EndpointResults;
using Shared.Security;

namespace EmployeeService.Web.Controllers;

[ApiController]
[Route("api/employees")]
[Authorize]
public class EmployeeController : ControllerBase
{
    [HttpPost]
    [RequireCanEdit]
    [EnableRateLimiting("write")]
    public async Task<EndpointResult<Guid>> Hire(
        [FromServices] HireEmployeeHandler handler,
        HireEmployeeCommand command,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Always the caller's own identity, never whatever the request body
        // happened to carry - HiredByAccountId only exists to answer "who did
        // this" later (e.g. notifying them if account provisioning fails), not
        // as client-supplied data.
        var hiredBy = Guid.Parse(User.FindFirstValue("sub")!);
        command = command with { HiredByAccountId = hiredBy, IdempotencyKey = idempotencyKey };
        return await handler.Handle(command, cancellationToken);
    }

    // The employee with their wallet, read from this service's copy of it (ADR 0034). A caller that has just changed the
    // wallet passes the version it was given (X-Wallet-Version on the grant) in X-Min-Wallet-Version and the read waits for the
    // copy to reach it; if it does not within the limit, the card still comes back, marked as behind.
    [HttpGet("{employeeId:guid}/card")]
    public async Task<ActionResult<EmployeeCardDto>> Card(
        [FromRoute] Guid employeeId,
        [FromServices] GetEmployeeCardHandler handler,
        [FromHeader(Name = "X-Min-Wallet-Version")] int? minWalletVersion,
        CancellationToken cancellationToken)
    {
        var card = await handler.Handle(employeeId, minWalletVersion, cancellationToken);
        if (card is null)
        {
            return NotFound();
        }

        Response.Headers["X-Card-Consistent"] = card.Consistent ? "true" : "false";
        return card;
    }

    [HttpPut("{employeeId:guid}/transfer")]
    [RequireCanEdit]
    [EnableRateLimiting("write")]
    public async Task<EndpointResult> Transfer(
        [FromRoute] Guid employeeId,
        [FromServices] TransferEmployeeHandler handler,
        [FromBody] TransferEmployeeRequest request,
        CancellationToken cancellationToken)
    {
        var command = new TransferEmployeeCommand(employeeId, request.DepartmentId, request.PositionId);
        return await handler.Handle(command, cancellationToken);
    }

    [HttpDelete("{employeeId:guid}")]
    [RequireCanEdit]

    // Terminating an employee is exactly the kind of "important" action GitHub-style sudo
    // mode exists for - irreversible, high-blast-radius, worth one extra re-verification
    // even from an already-signed-in admin. Multiple [Authorize]-family attributes combine
    // with AND semantics, so this adds to CanEdit rather than replacing it.
    [RequireStepUp]
    [EnableRateLimiting("write")]
    public async Task<EndpointResult> Terminate(
        [FromRoute] Guid employeeId,
        [FromServices] TerminateEmployeeHandler handler,
        CancellationToken cancellationToken) =>
        await handler.Handle(new TerminateEmployeeCommand(employeeId), cancellationToken);

    // Erasing a person's data (ADR 0048): the record this service owns. Irreversible, so it takes the same step-up as terminating.
    [HttpPost("{employeeId:guid}/erase")]
    [RequireCanEdit]
    [RequireStepUp]
    [EnableRateLimiting("write")]
    public async Task<EndpointResult> Erase(
        [FromRoute] Guid employeeId,
        [FromServices] EraseEmployeeHandler handler,
        CancellationToken cancellationToken) =>
        await handler.Handle(new EraseEmployeeCommand(employeeId), cancellationToken);

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