using System.Text;
using System.Threading.RateLimiting;
using JiraUnpacked.Api.Tenancy;
using JiraUnpacked.Infrastructure;
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

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<JiraUnpacked.Api.Tenancy.TenantScopeMiddleware>();
app.MapControllers();

app.Run();

// Exposed so the test project can drive the API with WebApplicationFactory<Program>.
public partial class Program { }
