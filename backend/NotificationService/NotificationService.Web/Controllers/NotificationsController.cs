using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NotificationService.Infrastructure.Postgres;
using Shared;

namespace NotificationService.Web.Controllers;

public record NotificationDto(
    Guid Id, string Type, string Title, string Body, string? DeepLink, bool IsRead, DateTime CreatedAt);

public record UnreadCountDto(int Count);

// Recipient is always the caller's own "sub" claim, never a route/query parameter - same
// principle as every other read endpoint in this codebase (RewardsController.GetOwnWallet,
// EmployeeController.Hire's actor). Nobody can list or mark read anyone else's notifications.
[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController(NotificationDbContext dbContext) : ControllerBase
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
