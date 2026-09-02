using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Security;
using JiraUnpacked.Infrastructure.Identity;
using JiraUnpacked.Infrastructure.Jira;
using JiraUnpacked.Infrastructure.Messaging;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting;
using JiraUnpacked.Infrastructure.Reporting.GapChecks;
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

        // Read-only + BYPASSRLS, for the background readers. Required rather than defaulted to
        // 'Default': a silent fallback to the owner would make this improvement optional, and
        // the failure it prevents is invisible.
        var readerConnectionString = configuration.GetConnectionString("Reader");
        if (string.IsNullOrWhiteSpace(readerConnectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'Reader' (the read-only cross-tenant role) is not configured.");
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
        services.AddScoped<TokenEnvelopeUpgrader>();
        services.AddScoped<IInviteCodeService, InviteCodeService>();
        services.AddSingleton<PasswordPolicy>();
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        // SMTP when configured, the log-only development sender otherwise. Which one is in use
        // is not left to chance outside Development: ProductionConfigGate refuses to start when
        // the fallback would be selected.
        var email = configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>();
        if (email?.IsConfigured == true)
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        else
            services.AddSingleton<IEmailSender, LoggingEmailSender>();
        services.AddScoped<IRegistrationService, RegistrationService>();
        services.AddScoped<IEmailVerificationService, EmailVerificationService>();
        services.Configure<AppOptions>(configuration.GetSection(AppOptions.SectionName));
        services.AddScoped<IPasswordResetService, PasswordResetService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IAccountDeletionService, JiraUnpacked.Infrastructure.Services.AccountDeletionService>();
        services.AddScoped<IJiraConnectionService, JiraConnectionService>();
        services.AddScoped<IJiraProjectService, JiraProjectService>();
        services.AddScoped<IJiraQueryService, JiraQueryService>();
        services.AddScoped<IProjectMappingService, ProjectMappingService>();
        services.AddScoped<IIssueTypeClassService, IssueTypeClassService>();
        services.AddHttpClient<IJiraApiClient, JiraApiClient>()
                .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));

        services.AddSingleton<IPrivilegedDbContextFactory, PrivilegedDbContextFactory>();
        services.AddScoped<IDueProjectFinder, DueProjectFinder>();
        services.AddScoped<ISyncOrchestrator, SyncOrchestrator>();
        services.AddScoped<ISyncRunQuery, SyncRunQuery>();
        services.AddScoped<IChangelogIngestService, ChangelogIngestService>();
        services.AddScoped<IDerivationService, DerivationService>();
        services.AddScoped<IIssueQueryService, IssueQueryService>();
        services.AddScoped<JiraUnpacked.Infrastructure.Reporting.Filtering.IIssueFilterTranslator,
                           JiraUnpacked.Infrastructure.Reporting.Filtering.IssueFilterTranslator>();
        services.AddScoped<JiraUnpacked.Infrastructure.Reporting.Filtering.IIssueFilterCompiler,
                           JiraUnpacked.Infrastructure.Reporting.Filtering.IssueFilterCompiler>();
        // Stateless projection over a static, compile-time-fixed dictionary — safe as a singleton.
        services.AddSingleton<IFilterCatalogService,
                               JiraUnpacked.Infrastructure.Reporting.Filtering.FilterCatalogService>();
        // Singleton on purpose: the point is to share one project's fold across the separate
        // requests a metrics page makes. Tenant scoping is inside the cache, keyed from
        // ICurrentTenant rather than from any caller.
        services.AddSingleton<IReportCache, ReportCache>();
        services.AddScoped<IMetricsQueryService, MetricsQueryService>();
        services.AddScoped<IGapCheck, OpenEpicAllChildrenDoneCheck>();
        services.AddScoped<IGapCheck, DoneEpicOpenChildrenCheck>();
        services.AddScoped<IGapCheckService, GapCheckRunner>();
        services.AddScoped<ISavedFilterService, SavedFilterService>();

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
