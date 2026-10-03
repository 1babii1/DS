using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Hosting;
using Xunit.Abstractions;

namespace AuthService.IntegrationTests;

// A flood of real logins (each one hashes a password, the expensive thing an unauthenticated caller can ask for) against an AuthService
// whose overall limit is small enough to be reached, with and without the credentials class of its own (ADR 0045). The service also has to
// keep answering the discovery document other services and clients fetch. Real Postgres, real Identity password hashing, the real middleware.
public class CredentialsFloodTests(ITestOutputHelper output)
{
    private sealed class Flooded(bool withClass) : AuthTestWebFactory
    {
        protected override int RateLimitPermits => 1_000_000;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("LoadShedding:MaxConcurrentRequests", "8");
            builder.UseSetting("LoadShedding:QueueLimit", "8");
            builder.UseSetting("LoadShedding:MaxQueueWait", "00:00:00.150");

            // The shipped class, kept with smaller numbers so that it fits inside the overall limit of 8; or the same paths with a limit so high it is no class at all.
            builder.UseSetting("LoadShedding:Classes:credentials:MaxConcurrentRequests", withClass ? "3" : "100000");
            builder.UseSetting("LoadShedding:Classes:credentials:QueueLimit", withClass ? "3" : "100000");
        }
    }

    private sealed record Outcome(int Served, int Refused, int LoginsDone);

    private static async Task<Outcome> Run(AuthTestWebFactory factory, TimeSpan duration)
    {
        const string email = "flood@test.local";
        const string password = "TestPass123";
        using var setup = factory.CreateClient();
        (await setup.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password))).EnsureSuccessStatusCode();
        // An unconfirmed account is turned away before its password is looked at, so the e-mail is confirmed: a login with the right password is what hashes.
        await setup.GetAsync(factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);
        var logins = 0;
        var stop = Stopwatch.StartNew();

        var flood = Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(async () =>
        {
            using var client = factory.CreateClient();
            while (stop.Elapsed < duration)
            {
                using var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref logins);
                }
            }
        })));

        var served = 0;
        var refused = 0;
        using var probe = factory.CreateClient();
        await Task.Delay(300);
        while (stop.Elapsed < duration)
        {
            using var response = await probe.GetAsync("/.well-known/openid-configuration");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                served++;
            }
            else
            {
                refused++;
            }

            await Task.Delay(30);
        }

        await flood;
        return new Outcome(served, refused, logins);
    }

    [Fact]
    public async Task A_login_flood_keeps_the_discovery_document_available_when_the_credential_endpoints_have_a_class_of_their_own()
    {
        var plain = new Flooded(withClass: false);
        var walled = new Flooded(withClass: true);
        await plain.InitializeAsync();
        await walled.InitializeAsync();
        try
        {
            var without = await Run(plain, TimeSpan.FromSeconds(5));
            var with = await Run(walled, TimeSpan.FromSeconds(5));

            output.WriteLine($"no class:   discovery served={without.Served} refused={without.Refused}; logins done={without.LoginsDone}");
            output.WriteLine($"with class: discovery served={with.Served} refused={with.Refused}; logins done={with.LoginsDone}");

            Assert.True(without.Refused > without.Served, "without a class the flood did not crowd out the discovery document; the experiment shows nothing");
            Assert.Equal(0, with.Refused);
            Assert.True(with.Served > 10);
            Assert.True(with.LoginsDone > 0, "no login got through in the walled service; the flood was not real logins");
        }
        finally
        {
            await plain.DisposeAsync();
            await walled.DisposeAsync();
        }
    }
}
