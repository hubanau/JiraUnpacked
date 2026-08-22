using System.Text;
using System.Threading.RateLimiting;
using JiraUnpacked.Api.Tenancy;
using JiraUnpacked.Infrastructure;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Identity;
using JiraUnpacked.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Quartz;

var builder = WebApplication.CreateBuilder(args);

// EF Core (Npgsql) + AppDbContext. Connection string resolved per environment.
builder.Services.AddInfrastructure(builder.Configuration);

// Bind + validate JWT options at startup (fail fast if the signing key is unsafe).
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.SigningKey) && o.SigningKey.Length >= 32,
        "Jwt:SigningKey must be configured with at least 32 characters.")
    .ValidateOnStart();

builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.SectionName));

builder.Services.Configure<JiraUnpacked.Infrastructure.Sync.SyncSchedulerOptions>(
    builder.Configuration.GetSection(JiraUnpacked.Infrastructure.Sync.SyncSchedulerOptions.SectionName));

builder.Services.Configure<JiraUnpacked.Infrastructure.Retention.RetentionOptions>(
    builder.Configuration.GetSection(JiraUnpacked.Infrastructure.Retention.RetentionOptions.SectionName));

builder.Services.Configure<JiraUnpacked.Infrastructure.Security.SecurityOptions>(
    builder.Configuration.GetSection(JiraUnpacked.Infrastructure.Security.SecurityOptions.SectionName));

if (builder.Configuration.GetValue("Sync:SchedulerEnabled", true))
{
    builder.Services.AddQuartz(q =>
    {
        var key = new Quartz.JobKey("sync-tick");
        q.AddJob<JiraUnpacked.Infrastructure.Sync.SyncTickJob>(key);
        q.AddTrigger(t => t.ForJob(key)
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(1).RepeatForever()));

        var retentionKey = new Quartz.JobKey("sync-run-retention");
        q.AddJob<JiraUnpacked.Infrastructure.Retention.SyncRunRetentionJob>(retentionKey);
        q.AddTrigger(t => t.ForJob(retentionKey)
            .WithSimpleSchedule(s => s.WithIntervalInHours(24).RepeatForever()));

        var reaperKey = new Quartz.JobKey("stuck-run-reaper");
        q.AddJob<JiraUnpacked.Infrastructure.Sync.StuckRunReaperJob>(reaperKey);
        q.AddTrigger(t => t.ForJob(reaperKey)
            .WithSimpleSchedule(s => s.WithIntervalInHours(1).RepeatForever()));
    });
    builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
}

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()!;

// JWT bearer authentication — validates tokens minted by JwtTokenService.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // keep raw claim names (sub/role/endUserId)
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorization(options =>
    options.AddPolicy("Admin", policy => policy.RequireRole("Admin")));

// Rate limiting for sensitive auth endpoints and expensive sync/rederive endpoints.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Per-IP: a brute-force attempt or one chatty client can't exhaust the window for everyone.
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 10, QueueLimit = 0 }));

    // Per-user: sync/rederive are expensive and call Jira; partition by the authenticated tenant.
    options.AddPolicy("sync", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst("endUserId")?.Value
                          ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 5, QueueLimit = 0 }));
});

builder.Services
    .AddControllers(o => o.Filters.Add<JiraUnpacked.Api.Filters.JiraApiExceptionFilter>())
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Refuse to serve outside Development with configuration that is only safe locally. Runs
// before the seeder so a misconfigured deployment fails immediately rather than after writing
// an Admin account with a dev-key-signed session.
JiraUnpacked.Api.Security.ProductionConfigGate.ThrowIfInvalid(
    app.Environment.IsDevelopment(),
    builder.Configuration[$"{JwtOptions.SectionName}:SigningKey"],
    builder.Configuration["TokenProtection:Key"],
    app.Services.GetRequiredService<
        Microsoft.Extensions.Options.IOptions<JiraUnpacked.Infrastructure.Messaging.EmailOptions>>()
        .Value.IsConfigured);

// Seed configured Admin accounts before serving requests (idempotent; FR-A3).
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AdminSeeder>().SeedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

var securityOptions = app.Services.GetRequiredService<
    Microsoft.Extensions.Options.IOptions<JiraUnpacked.Infrastructure.Security.SecurityOptions>>().Value;

// FIRST in the pipeline, before anything reads the scheme or the caller's address.
// Behind a TLS-terminating proxy the app otherwise sees the proxy's address on every request,
// which collapses the per-IP auth rate limiter into one shared bucket, and sees plain HTTP,
// which makes UseHttpsRedirection below redirect forever. Opt-in because trusting these
// headers when the app is directly reachable would let any client pick its own rate-limit
// bucket - see SecurityOptions.TrustProxyHeaders.
if (JiraUnpacked.Api.Security.ProxyHeaderGate.ShouldTrustProxyHeaders(securityOptions.TrustProxyHeaders))
{
    app.UseForwardedHeaders(
        JiraUnpacked.Api.Security.ProxyHeaderGate.Build(securityOptions.ForwardLimit));
}

if (JiraUnpacked.Api.Security.HstsGate.ShouldEnableHsts(app.Environment.IsDevelopment(), securityOptions.HstsEnabled))
{
    app.UseHsts();
}
app.UseHttpsRedirection();

// Liveness and readiness, anonymous and deliberately terse: an orchestrator needs a status
// code, and an unauthenticated caller must not learn anything about the deployment from the
// body. Mapped before the SPA fallback so it is never answered with index.html.
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
// Serves the built SPA from wwwroot. Before authentication so the bundle and
// its assets load without a token. No-ops when wwwroot is absent (tests, fresh
// clones), which is why the integration suite is unaffected.
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
// Runs after authentication/authorization so the "sync" policy's partition key
// (the endUserId claim) is resolved from an authenticated HttpContext.User —
// otherwise it always sees the unauthenticated principal and silently falls
// back to the IP key, letting different users behind the same IP share a bucket.
app.UseRateLimiter();
app.UseMiddleware<JiraUnpacked.Api.Tenancy.TenantScopeMiddleware>();
app.MapControllers();
// An unknown /api path must remain a JSON 404. Without this terminal route the
// SPA fallback below would answer it with index.html and HTTP 200, and clients
// would parse HTML as JSON. Catch-all routes have the lowest precedence, so
// every real controller route still wins.
//
// Deliberate, known trade-off: Map() (unlike MapGet/MapPost/...) matches
// every HTTP method, so a request that hits a real route with the wrong
// verb - e.g. POST /api/auth/me, which only accepts GET - now falls through
// to this catch-all and gets 404 Not Found instead of the framework's usual
// 405 Method Not Allowed. Acceptable for a SPA API: callers still get an
// unambiguous failure, just not the most specific status code.
app.Map("/api/{**rest}", () => Results.NotFound());
// Any other unmatched path serves the SPA shell so client-side routing works on
// a deep-link refresh.
app.MapFallbackToFile("index.html");

app.Run();

// Exposed so the test project can drive the API with WebApplicationFactory<Program>.
public partial class Program { }
