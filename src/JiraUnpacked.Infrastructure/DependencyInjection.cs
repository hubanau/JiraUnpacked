using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Security;
using JiraUnpacked.Infrastructure.Identity;
using JiraUnpacked.Infrastructure.Jira;
using JiraUnpacked.Infrastructure.Messaging;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Security;
using JiraUnpacked.Infrastructure.Services;
using JiraUnpacked.Infrastructure.Sync;
using JiraUnpacked.Infrastructure.Tenancy;
using JiraUnpacked.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'Default' is not configured.");
        }

        // The runtime context MUST use the RLS-enforced 'App' role. Never fall back to
        // 'Default' (the privileged owner) — that would silently bypass RLS (Layer 2).
        var appConnectionString = configuration.GetConnectionString("App");
        if (string.IsNullOrWhiteSpace(appConnectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'App' (the RLS-enforced role) is not configured.");
        }

        services.AddSingleton<AmbientCurrentTenant>();
        services.AddSingleton<ICurrentTenant>(sp => sp.GetRequiredService<AmbientCurrentTenant>());
        services.AddSingleton<ITenantScope>(sp => sp.GetRequiredService<AmbientCurrentTenant>());

        services.AddScoped<TenantConnectionInterceptor>();
        services.AddDbContext<AppDbContext>((sp, options) =>
            options.UseNpgsql(appConnectionString)
                   .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<AdminSeeder>();
        services.AddScoped<IInviteCodeService, InviteCodeService>();
        services.AddSingleton<PasswordPolicy>();
        services.AddSingleton<IEmailSender, LoggingEmailSender>();
        services.AddScoped<IRegistrationService, RegistrationService>();
        services.AddScoped<IEmailVerificationService, EmailVerificationService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IJiraConnectionService, JiraConnectionService>();
        services.AddScoped<IJiraProjectService, JiraProjectService>();
        services.AddScoped<IJiraQueryService, JiraQueryService>();
        services.AddScoped<IProjectMappingService, ProjectMappingService>();
        services.AddHttpClient<IJiraApiClient, JiraApiClient>()
                .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));

        services.AddSingleton<IPrivilegedDbContextFactory, PrivilegedDbContextFactory>();
        services.AddScoped<IDueProjectFinder, DueProjectFinder>();
        services.AddScoped<ISyncOrchestrator, SyncOrchestrator>();
        services.AddScoped<ISyncRunQuery, SyncRunQuery>();
        services.AddScoped<IChangelogIngestService, ChangelogIngestService>();
        services.AddScoped<IDerivationService, DerivationService>();

        services.AddOptions<TokenProtectionOptions>()
            .Configure(o =>
            {
                var section = configuration.GetSection(TokenProtectionOptions.SectionName);
                o.Key = section["Key"];
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.Key) && IsThirtyTwoByteBase64(o.Key),
                "TokenProtection:Key must be a base64-encoded 32-byte key.")
            .ValidateOnStart();
        services.AddSingleton<ITokenProtector, AesGcmTokenProtector>();

        return services;
    }

    private static bool IsThirtyTwoByteBase64(string value)
    {
        try { return Convert.FromBase64String(value).Length == 32; }
        catch (FormatException) { return false; }
    }
}
