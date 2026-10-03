using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NotificationService.Infrastructure.Postgres;
using Shared.Security;

namespace NotificationService.Web.Controllers;

public record EraseSubjectsRequest(string[]? Subjects);

public record EraseSubjectsResponse(int Requested, int NotificationsDeleted, int LinksDeleted);

[ApiController]
[Route("api/notifications/subjects")]
[Authorize]
public class SubjectsController(SubjectErasure erasure) : ControllerBase
{
    /// <summary>
    /// Erases what this service holds about these subjects (account ids or employee ids): the notifications addressed to their accounts and the
    /// link between employee and account, and marks them so that later events create nothing (ADR 0047). Administrators only, with a fresh step-up.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [HttpPost("erase")]
    [Authorize(Policy = "IsAdmin")]
    [RequireStepUp]
    public async Task<ActionResult<EraseSubjectsResponse>> Erase([FromBody] EraseSubjectsRequest request, CancellationToken cancellationToken)
    {
        var subjects = (request.Subjects ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct().ToArray();
        if (subjects.Length is 0 or > 50)
        {
            return BadRequest(new { detail = "Name between 1 and 50 subjects." });
        }

        var result = await erasure.EraseAsync(subjects, cancellationToken);
        return new EraseSubjectsResponse(subjects.Length, result.Notifications, result.Lookups);
    }
}
