using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RewardsService.Infrastructure;
using Shared.Ops;
using Shared.Security;

namespace RewardsService.Web.Controllers;

public record EraseSubjectsRequest(string[]? Subjects);

public record EraseSubjectsResponse(int Requested, int WalletsAnonymised, int LedgerRowsAnonymised);

[ApiController]
[Route("api/rewards/subjects")]
[Authorize]
public class SubjectsController(LedgerErasure erasure) : ControllerBase
{
    /// <summary>
    /// Anonymises the ledger of these subjects (employee ids or account ids): the wallet's history moves to an id nobody holds, reasons are
    /// blanked, a grantor's account id is removed from what they granted; amounts, dates and totals stay (ADR 0050). Administrators only, with a
    /// fresh step-up.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [HttpPost("erase")]
    [Authorize(Policy = OpsPolicy.Name)]
    [RequireStepUp]
    public async Task<ActionResult<EraseSubjectsResponse>> Erase([FromBody] EraseSubjectsRequest request, CancellationToken cancellationToken)
    {
        var subjects = (request.Subjects ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct().ToArray();
        if (subjects.Length is 0 or > 50)
        {
            return BadRequest(new { detail = "Name between 1 and 50 subjects." });
        }

        var result = await erasure.EraseAsync(subjects, cancellationToken);
        return new EraseSubjectsResponse(subjects.Length, result.Wallets, result.Transactions);
    }
}
