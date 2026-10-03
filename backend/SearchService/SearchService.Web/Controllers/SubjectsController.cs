using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Security;

namespace SearchService.Web.Controllers;

public record EraseSubjectsRequest(string[]? Subjects);

public record EraseSubjectsResponse(int Requested, int EmployeeDocumentsDeleted, long AuditDocumentsDeleted);

[ApiController]
[Route("api/search/subjects")]
[Authorize]
public class SubjectsController(SubjectErasure erasure) : ControllerBase
{
    /// <summary>
    /// Erases what the index holds about these subjects (employee ids, account ids, or an address that tried to sign in) and marks them so
    /// later events index nothing (ADR 0047). Administrators only, with a fresh step-up.
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
        return new EraseSubjectsResponse(subjects.Length, result.EmployeeDocuments, result.AuditDocuments);
    }
}
