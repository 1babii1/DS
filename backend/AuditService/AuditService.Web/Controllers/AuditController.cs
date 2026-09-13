using AuditService.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared;

namespace AuditService.Web.Controllers;

/// <param name="Id">Идентификатор записи журнала.</param>
/// <param name="SourceService">Сервис, опубликовавший событие.</param>
/// <param name="EventType">Тип события.</param>
/// <param name="AggregateId">Идентификатор сущности, к которой относится событие.</param>
/// <param name="Payload">Тело события в том виде, в каком его отправил producer.</param>
/// <param name="OccurredAt">Момент, зафиксированный при записи события.</param>
/// <param name="ReceivedAt">Момент, когда событие обработал консьюмер.</param>
public record AuditEntryDto(
    Guid Id,
    string SourceService,
    string EventType,
    string AggregateId,
    string Payload,
    DateTime OccurredAt,
    DateTime ReceivedAt);

[ApiController]
[Route("api/audit")]
[Authorize]
public class AuditController(AuditDbContext dbContext) : ControllerBase
{
    /// <summary>
    /// Страница журнала, новые записи первыми. Размер страницы ограничен сверху
    /// PagedResponse.MaxSize: журнал растёт бесконечно, и запрос без потолка означал
    /// возможность вытащить его целиком одним вызовом.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedResponse<AuditEntryDto>>> List(
        [FromQuery] string? aggregateId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        // Нормализация, а не 400: page=0 раньше приводил к Skip с отрицательным
        // значением, то есть к 500 на вполне безобидном запросе.
        var (currentPage, size) = PagedResponse<AuditEntryDto>.Normalize(page, pageSize);

        var query = dbContext.Entries.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(aggregateId))
        {
            query = query.Where(e => e.AggregateId == aggregateId);
        }

        var total = await query.CountAsync(cancellationToken);
        if (total == 0)
        {
            return PagedResponse<AuditEntryDto>.Empty(currentPage, size);
        }

        var entries = await query
            .OrderByDescending(e => e.OccurredAt)
            .Skip((currentPage - 1) * size)
            .Take(size)
            .Select(e => new AuditEntryDto(
                e.Id,
                e.SourceService,
                e.EventType,
                e.AggregateId,
                e.Payload,
                e.OccurredAt,
                e.ReceivedAt))
            .ToListAsync(cancellationToken);

        return new PagedResponse<AuditEntryDto>(entries, currentPage, size, total);
    }
}
