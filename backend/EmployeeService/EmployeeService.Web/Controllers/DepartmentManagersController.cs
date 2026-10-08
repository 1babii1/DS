using EmployeeService.Application.Authorization;
using EmployeeService.Application.Directory;
using EmployeeService.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Shared.Security;

namespace EmployeeService.Web.Controllers;

public record DepartmentManagersResponse(Guid DepartmentId, IReadOnlyList<Guid> Managers);

/// <summary>
/// Who manages a department (ADR 0057). Only meaningful in tree mode, where it is what a non-administrator's right to hire into a department
/// rests on; in roles mode the answer is a conflict rather than a tuple nobody reads. Changing it is for administrators, and takes a fresh step-up
/// because it hands out the right to hire.
/// </summary>
[ApiController]
[Route("api/employees/departments/{departmentId:guid}/managers")]
[Authorize]
public class DepartmentManagersController(
    IFgaClient store,
    IDirectoryLookupClient directory,
    IOptions<DepartmentAuthorizationOptions> options) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "IsAdmin")]
    public async Task<ActionResult<DepartmentManagersResponse>> List(Guid departmentId, CancellationToken cancellationToken)
    {
        if (NotInTreeMode() is { } conflict)
        {
            return conflict;
        }

        try
        {
            var tuples = await store.ReadAsync(FgaTuple.DepartmentId(departmentId), "manager", cancellationToken);
            var managers = tuples
                .Select(t => Guid.TryParse(t.User.AsSpan("user:".Length), out var id) ? id : (Guid?)null)
                .OfType<Guid>()
                .ToList();
            return new DepartmentManagersResponse(departmentId, managers);
        }
        catch (FgaException)
        {
            return StoreUnavailable();
        }
    }

    [HttpPut("{accountId:guid}")]
    [Authorize(Policy = "IsAdmin")]
    [RequireStepUp]
    public Task<ActionResult> Add(Guid departmentId, Guid accountId, CancellationToken cancellationToken) =>
        Change(departmentId, accountId, add: true, cancellationToken);

    [HttpDelete("{accountId:guid}")]
    [Authorize(Policy = "IsAdmin")]
    [RequireStepUp]
    public Task<ActionResult> Remove(Guid departmentId, Guid accountId, CancellationToken cancellationToken) =>
        Change(departmentId, accountId, add: false, cancellationToken);

    private async Task<ActionResult> Change(Guid departmentId, Guid accountId, bool add, CancellationToken cancellationToken)
    {
        if (NotInTreeMode() is { } conflict)
        {
            return conflict;
        }

        if (add)
        {
            // A manager of a department that does not exist would be a right nobody could ever use, and a typo nobody would notice.
            try
            {
                var department = await directory.GetDepartmentAsync(departmentId, cancellationToken);
                if (!department.Found)
                {
                    return NotFound(new { detail = "The department does not exist." });
                }
            }
            catch (DirectoryLookupException)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { detail = "DirectoryService is temporarily unavailable." });
            }
        }

        var tuple = new FgaTuple(FgaTuple.UserId(accountId), "manager", FgaTuple.DepartmentId(departmentId));
        try
        {
            await store.WriteAsync(add ? [tuple] : [], add ? [] : [tuple], cancellationToken);
        }
        catch (FgaException)
        {
            return StoreUnavailable();
        }

        return NoContent();
    }

    private ActionResult? NotInTreeMode() =>
        options.Value.Mode == DepartmentAuthorizationMode.Tree
            ? null
            : Conflict(new { detail = "Department authorization is in roles mode; managers are not used." });

    private ObjectResult StoreUnavailable() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new { detail = "The authorization store is temporarily unavailable." });
}
