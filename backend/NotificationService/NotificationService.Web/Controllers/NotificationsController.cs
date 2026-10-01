using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NotificationService.Infrastructure.Postgres;
using NotificationService.Web.HubTickets;
using Shared;

namespace NotificationService.Web.Controllers;

public record NotificationDto(
    Guid Id, string Type, string Title, string Body, string? DeepLink, bool IsRead, DateTime CreatedAt);

public record UnreadCountDto(int Count);

public record HubTicketDto(string Ticket, DateTimeOffset ExpiresAt);

// Recipient is always the caller's own "sub" claim, never a route/query parameter - same
// principle as every other read endpoint in this codebase (RewardsController.GetOwnWallet,
// EmployeeController.Hire's actor). Nobody can list or mark read anyone else's notifications.
[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController(NotificationDbContext dbContext, HubTicketService hubTickets) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<NotificationDto>>> List(
        [FromQuery] bool? unreadOnly,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId();
        var (currentPage, size) = PagedResponse<NotificationDto>.Normalize(page, pageSize);

        var query = dbContext.Notifications.AsNoTracking().Where(n => n.RecipientAccountId == accountId);
        if (unreadOnly == true)
        {
            query = query.Where(n => !n.IsRead);
        }

        var total = await query.CountAsync(cancellationToken);
        if (total == 0)
        {
            return PagedResponse<NotificationDto>.Empty(currentPage, size);
        }

        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((currentPage - 1) * size)
            .Take(size)
            .Select(n => new NotificationDto(n.Id, n.Type, n.Title, n.Body, n.DeepLink, n.IsRead, n.CreatedAt))
            .ToListAsync(cancellationToken);

        return new PagedResponse<NotificationDto>(items, currentPage, size, total);
    }

    // The BFF calls this as the signed-in person (their OAuth token, server to server) and gives the browser only the
    // ticket, which opens the hub and nothing else. Always for the caller's own account, never a parameter.
    [HttpPost("hub-ticket")]
    public ActionResult<HubTicketDto> IssueHubTicket()
    {
        var ticket = hubTickets.Issue(CurrentAccountId());
        return new HubTicketDto(ticket.Value, ticket.ExpiresAt);
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<UnreadCountDto>> UnreadCount(CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId();
        var count = await dbContext.Notifications.AsNoTracking()
            .CountAsync(n => n.RecipientAccountId == accountId && !n.IsRead, cancellationToken);

        return new UnreadCountDto(count);
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId();
        var notification = await dbContext.Notifications
            .SingleOrDefaultAsync(n => n.Id == id && n.RecipientAccountId == accountId, cancellationToken);

        if (notification is null)
        {
            return NotFound();
        }

        notification.MarkRead();
        await dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId();
        var unread = await dbContext.Notifications
            .Where(n => n.RecipientAccountId == accountId && !n.IsRead)
            .ToListAsync(cancellationToken);

        foreach (var notification in unread)
        {
            notification.MarkRead();
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    private Guid CurrentAccountId() => Guid.Parse(User.FindFirstValue("sub")!);
}
