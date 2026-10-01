using AuditService.Domain;
using AuditService.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
public class AuditController(AuditDbContext dbContext, IOptions<OrgChartOptions>? orgChartOptions = null) : ControllerBase
{
    private readonly int _maxEvents = orgChartOptions?.Value.MaxEvents ?? OrgChartOptions.DefaultMaxEvents;

    /// <summary>
    /// The organization as it was at an instant, rebuilt from the log: the department tree with the names and parents
    /// it had then and who worked in each, by name and position. Any signed-in user may read it (the same people are
    /// in the employee directory); it holds no contact detail, and the raw log stays admin-only. A bare date means the
    /// end of that UTC day, a time in the future is the present, and a log too large to replay is refused rather than
    /// answered from part of it.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [HttpGet("org-chart")]
    public async Task<ActionResult<OrgChartResponse>> OrgChart([FromQuery] string? at, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var instant = ParseInstant(at, now);
        if (instant is null)
        {
            return BadRequest(new { detail = "at must be a date (2026-03-05) or a time (2026-03-05T10:00:00Z) from the year 2000 on." });
        }

        var asOf = instant.Value > now ? now : instant.Value;

        var relevant = dbContext.Entries.AsNoTracking().Where(e => OrgReplay.EventTypes.Contains(e.EventType));
        var events = await relevant
            .Where(e => e.OccurredAt <= asOf)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .Take(_maxEvents + 1)
            .Select(e => new { e.EventType, e.Payload, e.OccurredAt })
            .ToListAsync(cancellationToken);
        if (events.Count > _maxEvents)
        {
            return UnprocessableEntity(new { detail = "The recorded history up to that time is too large to replay." });
        }

        var first = await relevant.MinAsync(e => (DateTime?)e.OccurredAt, cancellationToken);
        var snapshot = OrgReplay.At(
            asOf, events.Select((e, i) => new HistoricEvent(e.EventType, e.Payload, e.OccurredAt, i)));

        return new OrgChartResponse(snapshot.At, first, snapshot.Departments, snapshot.Unplaced, snapshot.SkippedEvents);
    }

    private static DateTime? ParseInstant(string? at, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(at))
        {
            return now;
        }

        var text = at.Trim();
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var bareDate = text.Length == 10
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", invariant, System.Globalization.DateTimeStyles.None, out _);
        if (!DateTime.TryParse(
                text,
                invariant,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed)
            || parsed.Year < 2000)
        {
            return null;
        }

        var utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return bareDate ? utc.Date.AddDays(1).AddTicks(-1) : utc;
    }

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