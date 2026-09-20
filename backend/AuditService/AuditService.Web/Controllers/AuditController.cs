using AuditService.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.Security;

namespace AuditService.Web.Controllers;

/// <param name="Id">Идентификатор записи журнала.</param>
/// <param name="SourceService">Сервис, опубликовавший событие.</param>
/// <param name="EventType">Тип события.</param>
/// <param name="AggregateId">Идентификатор сущности, к которой относится событие.</param>
/// <param name="Payload">
/// Тело события в том виде, в каком его отправил producer — только для админов, иначе null.
/// Payload содержит сырые поля события (для EmployeeHired это ФИО и email сотрудника), а
/// сам журнал доступен любому аутентифицированному пользователю, включая
/// самозарегистрировавшегося. SearchService по этой же причине намеренно не индексирует
/// payload — здесь он раньше отдавался напрямую.
/// </param>
/// <param name="OccurredAt">Момент, зафиксированный при записи события.</param>
/// <param name="ReceivedAt">Момент, когда событие обработал консьюмер.</param>
public record AuditEntryDto(
    Guid Id,
    string SourceService,
    string EventType,
    string AggregateId,
    string? Payload,
    DateTime OccurredAt,
    DateTime ReceivedAt);

/// <param name="Id">Идентификатор записи.</param>
/// <param name="MessageId">Идентификатор исходного сообщения из outbox.</param>
/// <param name="Topic">Kafka-топик, на котором сообщение получено.</param>
/// <param name="MessageKey">Ключ сообщения (aggregate id producer'а).</param>
/// <param name="Payload">Тело сообщения в том виде, в каком его отправил producer.</param>
/// <param name="Error">Текст последней ошибки обработки.</param>
/// <param name="AttemptCount">Сколько раз consumer пытался обработать сообщение до отказа.</param>
/// <param name="FailedAt">Момент, когда сообщение было окончательно признано необрабатываемым.</param>
public record DeadLetterDto(
    Guid Id,
    Guid MessageId,
    string Topic,
    string MessageKey,
    string Payload,
    string Error,
    int AttemptCount,
    DateTime FailedAt);

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
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
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

        var includePayload = User.IsInRole(RoleNames.Admin);

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
                includePayload ? e.Payload : null,
                e.OccurredAt,
                e.ReceivedAt))
            .ToListAsync(cancellationToken);

        return new PagedResponse<AuditEntryDto>(entries, currentPage, size, total);
    }

    /// <summary>
    /// Сообщения, которые consumer не смог обработать после всех попыток. Без этого
    /// эндпоинта их существование было бы видно только в критических логах - здесь
    /// они остаются доступны для разбора и ручного повторного воспроизведения.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [HttpGet("dead-letters")]
    [Authorize(Policy = "IsAdmin")]
    public async Task<ActionResult<PagedResponse<DeadLetterDto>>> ListDeadLetters(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var (currentPage, size) = PagedResponse<DeadLetterDto>.Normalize(page, pageSize);

        var query = dbContext.DeadLetters.AsNoTracking();

        var total = await query.CountAsync(cancellationToken);
        if (total == 0)
        {
            return PagedResponse<DeadLetterDto>.Empty(currentPage, size);
        }

        var entries = await query
            .OrderByDescending(e => e.FailedAt)
            .Skip((currentPage - 1) * size)
            .Take(size)
            .Select(e => new DeadLetterDto(
                e.Id,
                e.MessageId,
                e.Topic,
                e.MessageKey,
                e.Payload,
                e.Error,
                e.AttemptCount,
                e.FailedAt))
            .ToListAsync(cancellationToken);

        return new PagedResponse<DeadLetterDto>(entries, currentPage, size, total);
    }
}