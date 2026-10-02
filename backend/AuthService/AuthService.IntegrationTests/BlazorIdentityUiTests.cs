using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.IntegrationTests;

public sealed class BlazorIdentityUiTests(AuthTestWebFactory factory) : IClassFixture<AuthTestWebFactory>
{
    [Theory]
    [InlineData("/auth/sign-in")]
    [InlineData("/auth/sign-up")]
    [InlineData("/auth/password/forgot")]
    public async Task Public_identity_pages_render_the_shared_blazor_layout(string path)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync($"{path}?ReturnUrl=%2Fconnect%2Fauthorize%3Fclient_id%3Dportfolio-web");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("auth-card", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_in_rejects_a_return_url_outside_the_authorization_endpoint()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/auth/sign-in?ReturnUrl=%2Fdashboard");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid sign-in request", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_in_form_establishes_an_identity_session()
    {
        var email = $"blazor-sign-in-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await client.PostAsJsonAsync("/auth/register", new AuthService.Web.Contracts.RegisterRequest(email, password));
        await client.GetAsync(factory.EmailSender.Confirmations.Last(confirmation => confirmation.ToEmail == email).Link);

        const string returnUrl = "/connect/authorize?client_id=portfolio-web";
        var page = await client.GetAsync($"/auth/sign-in?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
        Assert.True(page.Headers.TryGetValues("Set-Cookie", out var cookies), "The sign-in page must set the antiforgery cookie.");
        Assert.NotEmpty(cookies);
        var markup = await page.Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex.Match(
            markup,
            "<input(?=[^>]*name=\"__RequestVerificationToken\")(?=[^>]*value=\"(?<token>[^\"]+)\")[^>]*>");
        Assert.True(token.Success, "The static SSR form must emit an antiforgery token.");

        var response = await client.PostAsync($"/auth/sign-in?ReturnUrl={Uri.EscapeDataString(returnUrl)}", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", System.Net.WebUtility.HtmlDecode(token.Groups["token"].Value)),
            new KeyValuePair<string, string>("Form.ReturnUrl", returnUrl),
            new KeyValuePair<string, string>("Form.Email", email),
            new KeyValuePair<string, string>("Form.Password", password),
            new KeyValuePair<string, string>("_handler", "sign-in"),
        ]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/connect/authorize", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }
}
