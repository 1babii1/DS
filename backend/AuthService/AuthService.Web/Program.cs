using System.Threading.RateLimiting;
using AuthService.Application;
using AuthService.Application.Database;
using AuthService.Infrastructure.Postgres;
using AuthService.Web.Configuration;
using AuthService.Web.Consumers;
using Fido2NetLib;
using Microsoft.AspNetCore.Identity;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using Shared.Cors;
using Shared.HealthChecks;
using Shared.Middlewares;
using Shared.Observability;
using Shared.Outbox;
using Shared.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Host.UseSerilog((context, _, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .AddOtlpLogging(context.Configuration, "auth-service"));

builder.Services.AddObservability(builder.Configuration, "auth-service");

builder.Services.AddControllers();
builder.Services.AddRazorPages();
builder.Services.AddEnvelopeModelStateValidation();
builder.Services.AddOpenApi();

builder.Services.AddOptions<WebClientOptions>()
    .Bind(builder.Configuration.GetSection(WebClientOptions.SectionName))
    .Validate(
        options => options.IsValid(builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Docker")),
        "Enabled web client requires a strong client secret and an HTTPS frontend origin (loopback HTTP is local-only).")
    .ValidateOnStart();

builder.Services.AddFrameworkCors(builder.Configuration);

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

builder.Services.AddInfrastructurePostgres(builder.Configuration);

builder.Services.AddIdentityServices(builder.Environment);
builder.Services.AddOpenIddictServer(builder.Environment, builder.Configuration);
builder.Services.AddOpenIddictQuartzScheduler();

builder.Services.AddDatabaseHealthCheck<AuthDbContext>();

builder.Services.AddOptions<EmailOptions>()
    .BindConfiguration(EmailOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<AccountRecoveryService>();
builder.Services.AddScoped<TwoFactorService>();
builder.Services.AddScoped<SecurityAuditService>();
builder.Services.AddScoped<StepUpService>();
builder.Services.AddScoped<AdminAccountService>();
builder.Services.AddAdminPolicy();

builder.Services.AddMemoryCache();
builder.Services.AddScoped<PasskeyService>();

// RPID/Origins come from Auth:Issuer (the same value AddOpenIddictServer already reads for
// SetIssuer) rather than a second config entry: WebAuthn's relying-party identity is AuthService's
// own domain, since the passkey ceremonies run on its own Razor Pages, not the SPA's origin.
var issuerUri = new Uri(builder.Configuration["Auth:Issuer"] ?? "http://localhost:5100");
builder.Services.AddFido2(options =>
{
    options.RPID = issuerUri.Host;
    options.RPName = "DS";
    options.Origins = new HashSet<string> { issuerUri.GetLeftPart(UriPartial.Authority) };
});
builder.Services.AddStepUpPolicy();

// "Sign in with Google" - disabled unless a client id/secret is actually configured, same
// fallback reasoning as WebClientOptions.Enabled. SignInScheme is Identity's own external
// cookie (IdentityConstants.ExternalScheme), not the application cookie: it only needs to
// survive the round trip to Google and back, and ExternalLoginController is what turns it
// into a real Identity.Application session (linking to an existing account or provisioning
// a new one), the same two-step shape ASP.NET Identity's own external-login samples use.
builder.Services.AddOptions<GoogleOptions>()
    .Bind(builder.Configuration.GetSection(GoogleOptions.SectionName));
var googleOptions = new GoogleOptions();
builder.Configuration.GetSection(GoogleOptions.SectionName).Bind(googleOptions);
if (googleOptions.Enabled)
{
    builder.Services.AddAuthentication()
        .AddGoogle(options =>
        {
            options.ClientId = googleOptions.ClientId;
            options.ClientSecret = googleOptions.ClientSecret;
            options.SignInScheme = IdentityConstants.ExternalScheme;
            options.CallbackPath = "/auth/external/google/callback";
            GoogleClaimMapping.Apply(options);
        });
}

builder.Services.AddScoped<ExternalLoginService>();
builder.Services.AddScoped<AuthSessionService>();
builder.Services.AddScoped<EmailChangeService>();
builder.Services.AddScoped<AccountDeletionService>();
builder.Services.AddSingleton<SigningKeyProtector>();
builder.Services.AddScoped<SigningKeyStore>();

// Private signing/encryption keys must not sit in the database as plaintext in production: a
// database-only leak would otherwise hand over the keys that mint and decrypt every token.
if (builder.Environment.IsProduction()
    && string.IsNullOrWhiteSpace(builder.Configuration[SigningKeyProtector.ConfigurationKey]))
{
    throw new InvalidOperationException(
        $"{SigningKeyProtector.ConfigurationKey} must be set in Production (32 random bytes, base64: openssl rand -base64 32).");
}

builder.Services.AddHttpClient<IPasswordBreachChecker, HaveIBeenPwnedPasswordChecker>(client =>
{
    client.BaseAddress = new Uri("https://api.pwnedpasswords.com/");
    client.Timeout = TimeSpan.FromSeconds(3);
    client.DefaultRequestHeaders.Add("Add-Padding", "true");
})

    // A blip against a third-party API shouldn't fall straight to "skip the check" - worth
    // one quick retry first. The breaker exists so a real HIBP outage stops adding latency
    // to every registration/password-reset request once it's established the API is down;
    // HaveIBeenPwnedPasswordChecker's own catch treats a tripped breaker the same as a
    // direct network failure and fails open either way.
    .AddResilienceHandler("hibp", builder =>
    {
        builder.AddRetry(new()
        {
            MaxRetryAttempts = 2,
            Delay = TimeSpan.FromMilliseconds(100),
            BackoffType = DelayBackoffType.Constant,
        });

        builder.AddCircuitBreaker(new()
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(15),
        });
    });

builder.Services.AddScoped<IOutboxWriter, OutboxWriter>();
builder.Services.AddOutboxPublisher<AuthDbContext>(builder.Configuration, "auth.events");

var kafkaBootstrapServers = builder.Configuration["Kafka:BootstrapServers"]
    ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set.");
var kafkaSecurity = KafkaSecurityOptions.FromConfiguration(builder.Configuration);

builder.Services.Configure<AuthConsumerOptions>(options =>
{
    options.BootstrapServers = kafkaBootstrapServers;
    options.Security = kafkaSecurity;
    options.Topics = builder.Configuration.GetSection("Kafka:Topics").Get<string[]>()
        ?? throw new InvalidOperationException("Configuration 'Kafka:Topics' is not set.");
    options.GroupId = builder.Configuration["Kafka:GroupId"] ?? "auth-service";
});
builder.Services.AddHostedService<EmployeeEventsConsumer>();
builder.Services.AddKafkaHealthCheck(kafkaBootstrapServers, kafkaSecurity);

// Partitioned by client IP, not global: a global limiter would let one abusive IP
// lock out every other user sharing the same bucket. Five attempts a minute is
// tight enough to blunt credential stuffing without a legitimate user who mistypes
// their password twice ever noticing.
// Bound to configuration, not hardcoded, so integration tests can raise the limit
// instead of tripping it on their own traffic - TestServer's in-memory transport
// never populates RemoteIpAddress, so every test request would otherwise land in
// the same "unknown" partition and exhaust the limit after five calls regardless of
// which test made them. Defaults match what was hardcoded before.
var authRateLimit = builder.Configuration.GetValue("RateLimiting:Auth:PermitLimit", 5);
var authRateLimitWindow = builder.Configuration.GetValue("RateLimiting:Auth:WindowSeconds", 60);

// Must be configured for the rate limiter below to see the real client IP
// instead of nginx's - see ForwardedHeadersExtensions for why that matters.
builder.Services.AddProxyForwardedHeaders(builder.Configuration);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authRateLimit,
            Window = TimeSpan.FromSeconds(authRateLimitWindow),
            QueueLimit = 0,
        }));
});

var app = builder.Build();

app.UseProxyForwardedHeaders();

app.UseRequestCorrelationId();
app.UseExceptionMiddleware();

app.UseSerilogRequestLogging();

app.MapOpenApi("/openapi/v1/swagger.json");
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1/swagger.json", "AuthService"));

app.ConfigureCors();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapRazorPages();
app.MapDefaultHealthChecks();

await SigningKeySeeder.SeedAsync(app.Services, app.Configuration);
await OpenIddictSeeder.SeedAsync(app.Services);
await WebClientSeeder.SeedAsync(app.Services);

app.Run();

namespace AuthService.Web
{
    public partial class Program;
}