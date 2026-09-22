using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AuthService.Domain;
using AuthService.Web.Configuration;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests;

// ExternalLoginService.CompleteAsync is the account-linking/provisioning decision behind
// "Sign in with Google" - exercised directly with a hand-built ExternalLoginInfo rather than
// through ExternalLoginController, since driving a real Google authorization round trip
// needs a live Google OAuth client this project does not have configured. The controller
// itself (challenge/callback plumbing) is thin enough that this is the meaningful thing to
// prove; what ASP.NET Core's own Google handler does with a real code exchange is Microsoft's
// to test, not this codebase's.
public class ExternalLoginTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private const string Provider = "Google";

    // AuthenticationConfiguration renames Identity's application cookie; asserting on the
    // scheme name instead would pass or fail for the wrong reason.
    private const string SessionCookieName = "AuthServiceCookie";

    private readonly AuthTestWebFactory _factory;
    private readonly Func<Task> _resetDatabase;

    public ExternalLoginTests(AuthTestWebFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task A_new_email_provisions_a_confirmed_viewer_account_with_no_password()
    {
        var email = $"external-{Guid.NewGuid():N}@gmail.com";
        var providerKey = Guid.NewGuid().ToString("N");

        var (outcome, user) = await CompleteAsync(email, providerKey);

        Assert.Equal(ExternalLoginService.Outcome.SignedIn, outcome);
        Assert.NotNull(user);
        Assert.True(user!.EmailConfirmed);

        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        Assert.True(await userManager.IsInRoleAsync(user, RoleNames.Viewer));
        Assert.False(await userManager.HasPasswordAsync(user));
    }

    [Fact]
    public async Task A_second_sign_in_with_the_same_provider_key_reuses_the_same_account()
    {
        var email = $"external-{Guid.NewGuid():N}@gmail.com";
        var providerKey = Guid.NewGuid().ToString("N");
        var (_, firstUser) = await CompleteAsync(email, providerKey);

        var (outcome, secondUser) = await CompleteAsync(email, providerKey);

        Assert.Equal(ExternalLoginService.Outcome.SignedIn, outcome);
        Assert.Equal(firstUser!.Id, secondUser!.Id);
    }

    [Fact]
    public async Task A_google_login_matching_an_existing_confirmed_accounts_email_links_to_it()
    {
        var email = $"external-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);

        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var existingAccount = await userManager.FindByEmailAsync(email);

        var (outcome, linkedUser) = await CompleteAsync(email, Guid.NewGuid().ToString("N"));

        Assert.Equal(ExternalLoginService.Outcome.SignedIn, outcome);
        Assert.Equal(existingAccount!.Id, linkedUser!.Id);
    }

    // Account pre-hijacking ("classic-federated merge", Microsoft Research / USENIX Security 2022):
    // an attacker registers victim@gmail.com with a password they know but never confirms it, then
    // the real owner signs in with Google. Linking the Google login to that pre-existing row would
    // hand the owner an account whose password the attacker still holds.
    [Fact]
    public async Task A_google_login_matching_an_UNCONFIRMED_account_does_not_keep_the_squatters_password()
    {
        var email = $"external-{Guid.NewGuid():N}@test.local";
        const string squatterPassword = "SquatterPass123";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, squatterPassword));

        string? stampBefore;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var before = await scope.ServiceProvider.GetRequiredService<UserManager<Account>>().FindByEmailAsync(email);
            Assert.False(before!.EmailConfirmed);
            Assert.True(await scope.ServiceProvider.GetRequiredService<UserManager<Account>>().HasPasswordAsync(before));
            stampBefore = before.SecurityStamp;
        }

        var (outcome, user) = await CompleteAsync(email, Guid.NewGuid().ToString("N"));

        Assert.Equal(ExternalLoginService.Outcome.SignedIn, outcome);
        await using var afterScope = _factory.Services.CreateAsyncScope();
        var userManager = afterScope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var after = await userManager.FindByIdAsync(user!.Id.ToString());
        Assert.True(after!.EmailConfirmed);
        Assert.False(await userManager.HasPasswordAsync(after));
        Assert.NotEqual(stampBefore, after.SecurityStamp);

        // The observable consequence: the squatter's password no longer opens the account.
        using var attacker = _factory.CreateClient();
        var login = await attacker.PostAsJsonAsync("/auth/login", new LoginRequest(email, squatterPassword));
        Assert.NotEqual(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Fact]
    public async Task Taking_over_an_unconfirmed_account_without_a_password_still_rotates_the_security_stamp()
    {
        // No password to remove here, so RemovePasswordAsync's own stamp update does not happen -
        // the explicit rotation is what invalidates the squatter's outstanding confirmation and
        // reset tokens (they embed the security stamp).
        var email = $"external-{Guid.NewGuid():N}@test.local";
        string? stampBefore;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var squatted = new Account { UserName = email, Email = email, EmailConfirmed = false, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Assert.True((await userManager.CreateAsync(squatted)).Succeeded);
            stampBefore = squatted.SecurityStamp;
        }

        var (outcome, user) = await CompleteAsync(email, Guid.NewGuid().ToString("N"));

        Assert.Equal(ExternalLoginService.Outcome.SignedIn, outcome);
        await using var afterScope = _factory.Services.CreateAsyncScope();
        var after = await afterScope.ServiceProvider.GetRequiredService<UserManager<Account>>().FindByIdAsync(user!.Id.ToString());
        Assert.True(after!.EmailConfirmed);
        Assert.NotEqual(stampBefore, after.SecurityStamp);
    }

    [Fact]
    public async Task A_confirmed_accounts_password_is_left_alone_when_linking_google()
    {
        var email = $"external-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);

        await CompleteAsync(email, Guid.NewGuid().ToString("N"));

        // Guards the fix above from over-reaching: only an UNCONFIRMED account is stripped.
        using var owner = _factory.CreateClient();
        var login = await owner.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Theory]
    [InlineData("false")]
    [InlineData(null)]
    public async Task A_provider_email_that_is_not_verified_is_rejected_without_creating_or_linking_anything(string? emailVerified)
    {
        var email = $"external-{Guid.NewGuid():N}@test.local";

        var (outcome, user) = await CompleteAsync(email, Guid.NewGuid().ToString("N"), emailVerified);

        Assert.Equal(ExternalLoginService.Outcome.EmailNotVerified, outcome);
        Assert.Null(user);
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<UserManager<Account>>().FindByEmailAsync(email));
    }

    [Theory]
    [InlineData("""{"email":"a@gmail.com","email_verified":true}""", true)]
    [InlineData("""{"email":"a@gmail.com","email_verified":false}""", false)]
    [InlineData("""{"email":"a@gmail.com"}""", false)]
    public void The_google_claim_mapping_turns_the_userinfo_email_verified_field_into_a_claim_the_service_honours(
        string userInfoJson, bool expectedVerified)
    {
        // Runs the real ClaimActions the AddGoogle registration installs against a userinfo-shaped
        // payload - proves the JSON boolean survives mapping (as "True"/"False") and that a missing
        // field fails closed, without needing a live Google round trip.
        var options = new Microsoft.AspNetCore.Authentication.Google.GoogleOptions();
        GoogleClaimMapping.Apply(options);
        var identity = new ClaimsIdentity("Test");
        using var json = System.Text.Json.JsonDocument.Parse(userInfoJson);
        foreach (var action in options.ClaimActions)
        {
            action.Run(json.RootElement, identity, "https://accounts.google.com");
        }

        Assert.Equal(expectedVerified, GoogleClaimMapping.IsEmailVerified(new ClaimsPrincipal(identity)));
    }

    [Fact]
    public async Task First_linking_google_into_an_account_with_two_factor_enabled_requires_the_second_factor()
    {
        var email = $"external-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var account = await userManager.FindByEmailAsync(email);
            await userManager.SetTwoFactorEnabledAsync(account!, true);
        }

        var providerKey = Guid.NewGuid().ToString("N");
        var (outcome, _, cookies) = await CompleteWithCookiesAsync(email, providerKey);

        Assert.Equal(ExternalLoginService.Outcome.TwoFactorRequired, outcome);
        Assert.Contains(IdentityConstants.TwoFactorUserIdScheme, cookies, StringComparison.Ordinal);
        Assert.DoesNotContain(SessionCookieName, cookies, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_already_linked_google_login_still_bypasses_the_second_factor()
    {
        var email = $"external-{Guid.NewGuid():N}@test.local";
        var providerKey = Guid.NewGuid().ToString("N");
        var (_, user) = await CompleteAsync(email, providerKey);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            await userManager.SetTwoFactorEnabledAsync((await userManager.FindByIdAsync(user!.Id.ToString()))!, true);
        }

        var (outcome, _, cookies) = await CompleteWithCookiesAsync(email, providerKey);

        // Google's own sign-in is a strong external factor; only the FIRST, email-matched link is gated.
        Assert.Equal(ExternalLoginService.Outcome.SignedIn, outcome);
        Assert.Contains(SessionCookieName, cookies, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_an_email_claim_fails_without_creating_an_account()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var externalLogins = scope.ServiceProvider.GetRequiredService<ExternalLoginService>();
        var identity = new ClaimsIdentity(authenticationType: "Test");
        var info = new ExternalLoginInfo(new ClaimsPrincipal(identity), Provider, Guid.NewGuid().ToString("N"), Provider);

        var (outcome, user) = await externalLogins.CompleteAsync(info, ipAddress: null, userAgent: null, CancellationToken.None);

        Assert.Equal(ExternalLoginService.Outcome.MissingEmail, outcome);
        Assert.Null(user);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private async Task<(ExternalLoginService.Outcome Outcome, Account? User)> CompleteAsync(
        string email, string providerKey, string? emailVerified = "true")
    {
        var (outcome, user, _) = await CompleteWithCookiesAsync(email, providerKey, emailVerified);
        return (outcome, user);
    }

    private async Task<(ExternalLoginService.Outcome Outcome, Account? User, string SetCookies)> CompleteWithCookiesAsync(
        string email, string providerKey, string? emailVerified = "true")
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        // SignInManager writes the Identity.Application cookie through HttpContext, which a
        // scope resolved outside a real request never has - ExternalLoginService is normally
        // only ever called from inside ExternalLoginController's own request, so this fake
        // context stands in for that here, the one seam a direct-service test needs to bridge.
        var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<Account>>();
        signInManager.Context = httpContext;

        var externalLogins = scope.ServiceProvider.GetRequiredService<ExternalLoginService>();
        var identity = new ClaimsIdentity(authenticationType: "Test");
        identity.AddClaim(new Claim(ClaimTypes.Email, email));
        if (emailVerified is not null)
        {
            identity.AddClaim(new Claim(GoogleClaimMapping.EmailVerifiedClaim, emailVerified));
        }

        var info = new ExternalLoginInfo(new ClaimsPrincipal(identity), Provider, providerKey, "Test User");

        var (outcome, user) = await externalLogins.CompleteAsync(info, ipAddress: null, userAgent: null, CancellationToken.None);
        return (outcome, user, string.Join(';', httpContext.Response.Headers.SetCookie.ToArray()));
    }
}
