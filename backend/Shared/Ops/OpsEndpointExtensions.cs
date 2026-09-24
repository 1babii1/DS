using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Shared.Kafka;
using Shared.Security;

namespace Shared.Ops;

public static class OpsEndpointExtensions
{
    // Every state-changing route also demands step-up (recent re-verification), the same bar
    // EmployeeService puts on terminating someone: re-injecting events is not routine.
    public static IEndpointRouteBuilder MapOutboxOps<TContext>(this IEndpointRouteBuilder app, string prefix)
        where TContext : DbContext
    {
        var group = app.MapGroup($"{prefix}/outbox").RequireAuthorization(OpsPolicy.Name);

        group.MapGet("parked", async (TContext db, int? limit, CancellationToken ct) =>
            Results.Ok(await OpsHandlers.ListParkedAsync(db, limit ?? 50, ct)));

        group.MapGet("parked/{id:guid}", async (TContext db, Guid id, CancellationToken ct) =>
            await OpsHandlers.GetParkedAsync(db, id, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());

        group.MapPost("parked/{id:guid}/redrive", async (TContext db, Guid id, ClaimsPrincipal user, CancellationToken ct) =>
            await OpsHandlers.RedriveAsync(db, id, user.FindFirstValue("sub") ?? "unknown", ct) switch
            {
                RedriveOutcome.Redriven => Results.NoContent(),
                RedriveOutcome.NotParked => Results.Conflict(),
                _ => Results.NotFound(),
            }).RequireAuthorization(StepUpAuthorizationExtensions.PolicyName);

        return app;
    }

    public static IEndpointRouteBuilder MapDeadLetterOps<TContext>(this IEndpointRouteBuilder app, string prefix)
        where TContext : DbContext, IHasDeadLetters
    {
        var group = app.MapGroup($"{prefix}/dead-letters").RequireAuthorization(OpsPolicy.Name);

        group.MapGet(string.Empty, async (TContext db, int? limit, CancellationToken ct) =>
            Results.Ok(await OpsHandlers.ListDeadLettersAsync(db, limit ?? 50, ct)));

        group.MapGet("{id:guid}", async (TContext db, Guid id, CancellationToken ct) =>
            await OpsHandlers.GetDeadLetterAsync(db, id, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());

        return app;
    }
}
