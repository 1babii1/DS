using AuditService.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuditService.Web.Controllers;

[ApiController]
[Route("api/audit")]
[Authorize]
public class AuditController(AuditDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? aggregateId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Entries.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(aggregateId))
        {
            query = query.Where(e => e.AggregateId == aggregateId);
        }

        var entries = await query
            .OrderByDescending(e => e.OccurredAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new
            {
                e.Id,
                e.SourceService,
                e.EventType,
                e.AggregateId,
                e.Payload,
                e.OccurredAt,
                e.ReceivedAt,
            })
            .ToListAsync(cancellationToken);

        return Ok(entries);
    }
}
