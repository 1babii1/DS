using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Shared.Security;

namespace McpServer.Agent;

public sealed record ConfirmPlanRequest(string? Token);

// What the approval card is drawn from. Built from the signed plan on the server: names and amounts the person is
// about to approve, no ids and not the token, and nothing the model wrote after the plan was made.
public sealed record PlanCard(DateTimeOffset ExpiresAt, bool NeedsReverification, IReadOnlyList<PlanCardStep> Steps);

public sealed record PlanCardStep(
    string Kind,
    string Summary,
    string? FullName,
    string? Email,
    string? EmployeeName,
    string? DepartmentName,
    string? PositionName,
    decimal? Amount,
    string? Reason);

public static class AgentEndpoints
{
    // The user's own decision, made over their own authenticated session. Not an MCP tool, so nothing the
    // model can call reaches it. The path sits under /mcp only so the existing gateway route covers it.
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/mcp/plans/confirm", async (
            ConfirmPlanRequest request,
            ClaimsPrincipal user,
            PlanSigner signer,
            PlanExecutor executor,
            IAuthorizationService authorization,
            AgentTelemetry telemetry,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
            {
                telemetry.Confirmation("rejected", "unauthenticated");
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.Token))
            {
                telemetry.Confirmation("rejected", "invalid");
                return Results.BadRequest(new { error = "A plan token is required." });
            }

            var opened = Open(signer, request.Token, userId, telemetry);
            if (opened.Failure is not null)
            {
                return opened.Failure;
            }

            var plan = opened.Plan!;

            // Handing out money is the step a click must not be enough for: the caller has to have
            // re-verified recently (the same "sudo mode" claim EmployeeService asks for on termination).
            // Checked here, after the plan is known to be genuine and theirs, and before anything runs.
            if (plan.Steps.Any(s => s.Kind == StepKind.GrantCurrency)
                && !(await authorization.AuthorizeAsync(user, StepUpAuthorizationExtensions.PolicyName)).Succeeded)
            {
                telemetry.Confirmation("rejected", "step_up_required");
                return Results.Json(
                    new { error = "This plan hands out currency, so it needs a recent re-verification. Verify again and confirm." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var report = await executor.ExecuteAsync(plan, ct);
            telemetry.Confirmation(report.Completed ? "completed" : "failed");
            return Results.Ok(report);
        }).RequireAuthorization();

        // What the person is about to approve, read from the signed plan. Runs nothing and asks nothing of
        // the services; it answers the same fixed messages as confirm for a plan that is not theirs, expired or not genuine.
        app.MapPost("/mcp/plans/preview", (
            ConfirmPlanRequest request,
            ClaimsPrincipal user,
            PlanSigner signer,
            AgentTelemetry telemetry) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.Token))
            {
                return Results.BadRequest(new { error = "A plan token is required." });
            }

            var opened = Open(signer, request.Token, userId, telemetry: null);
            if (opened.Failure is not null)
            {
                return opened.Failure;
            }

            var plan = opened.Plan!;
            return Results.Ok(new PlanCard(
                plan.ExpiresAt,
                plan.Steps.Any(s => s.Kind == StepKind.GrantCurrency),
                plan.Steps.Select(s => new PlanCardStep(
                    s.Kind.ToString(), s.Summary, s.FullName, s.Email, s.EmployeeName, s.DepartmentName, s.PositionName,
                    s.Amount, s.Reason)).ToList()));
        }).RequireAuthorization();

        return app;
    }

    // Fixed answers only: nothing about why a signature failed, or whose plan it was.
    private static (Plan? Plan, IResult? Failure) Open(PlanSigner signer, string token, Guid userId, AgentTelemetry? telemetry)
    {
        try
        {
            return (signer.Verify(token, userId), null);
        }
        catch (PlanTokenException ex)
        {
            switch (ex.Problem)
            {
                case PlanTokenProblem.WrongUser:
                    telemetry?.Confirmation("rejected", "wrong_user");
                    return (null, Results.Json(new { error = "This plan is not yours to confirm." }, statusCode: StatusCodes.Status403Forbidden));
                case PlanTokenProblem.Expired:
                    telemetry?.Confirmation("rejected", "expired");
                    return (null, Results.Json(new { error = "This plan has expired. Ask for a new one." }, statusCode: StatusCodes.Status410Gone));
                default:
                    telemetry?.Confirmation("rejected", "invalid");
                    return (null, Results.BadRequest(new { error = "This is not a valid plan." }));
            }
        }
    }
}
