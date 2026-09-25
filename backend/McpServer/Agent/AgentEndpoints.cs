using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Shared.Security;

namespace McpServer.Agent;

public sealed record ConfirmPlanRequest(string? Token);

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
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.Token))
            {
                return Results.BadRequest(new { error = "A plan token is required." });
            }

            Plan plan;
            try
            {
                plan = signer.Verify(request.Token, userId);
            }
            catch (PlanTokenException ex)
            {
                // Fixed answers only: nothing about why a signature failed, or whose plan it was.
                return ex.Problem switch
                {
                    PlanTokenProblem.WrongUser => Results.Json(new { error = "This plan is not yours to confirm." }, statusCode: StatusCodes.Status403Forbidden),
                    PlanTokenProblem.Expired => Results.Json(new { error = "This plan has expired. Ask for a new one." }, statusCode: StatusCodes.Status410Gone),
                    _ => Results.BadRequest(new { error = "This is not a valid plan." }),
                };
            }

            // Handing out money is the step a click must not be enough for: the caller has to have
            // re-verified recently (the same "sudo mode" claim EmployeeService asks for on termination).
            // Checked here, after the plan is known to be genuine and theirs, and before anything runs.
            if (plan.Steps.Any(s => s.Kind == StepKind.GrantCurrency)
                && !(await authorization.AuthorizeAsync(user, StepUpAuthorizationExtensions.PolicyName)).Succeeded)
            {
                return Results.Json(
                    new { error = "This plan hands out currency, so it needs a recent re-verification. Verify again and confirm." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Ok(await executor.ExecuteAsync(plan, ct));
        }).RequireAuthorization();

        return app;
    }
}
