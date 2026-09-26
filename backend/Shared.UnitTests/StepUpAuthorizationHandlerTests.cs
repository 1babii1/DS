using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Shared.Security;

namespace Shared.UnitTests;

// EmployeeController.Terminate is the one live endpoint gated on this today, but it goes
// through the full HTTP/JWT pipeline there - this exercises the actual decision the handler
// makes directly, the same way JwtAuthOptionsTests isolates the options-binding half of the
// same "how this platform authenticates a request" contract.
public class StepUpAuthorizationHandlerTests
{
    [Fact]
    public async Task A_token_with_no_elevated_until_claim_does_not_satisfy_the_requirement()
    {
        var succeeded = await EvaluateAsync(claims: []);

        Assert.False(succeeded);
    }

    [Fact]
    public async Task A_token_whose_elevation_has_expired_does_not_satisfy_the_requirement()
    {
        var expired = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds().ToString();

        var succeeded = await EvaluateAsync([new Claim(StepUpClaims.ElevatedUntilClaim, expired)]);

        Assert.False(succeeded);
    }

    [Fact]
    public async Task A_token_with_a_future_elevated_until_claim_satisfies_the_requirement()
    {
        var future = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString();

        var succeeded = await EvaluateAsync([new Claim(StepUpClaims.ElevatedUntilClaim, future)]);

        Assert.True(succeeded);
    }

    [Fact]
    public async Task A_malformed_elevated_until_claim_does_not_satisfy_the_requirement()
    {
        var succeeded = await EvaluateAsync([new Claim(StepUpClaims.ElevatedUntilClaim, "not-a-number")]);

        Assert.False(succeeded);
    }

    private static async Task<bool> EvaluateAsync(IEnumerable<Claim> claims)
    {
        var handler = new StepUpAuthorizationHandler();
        var requirement = new StepUpRequirement();
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);

        await handler.HandleAsync(context);

        return context.HasSucceeded;
    }
}
