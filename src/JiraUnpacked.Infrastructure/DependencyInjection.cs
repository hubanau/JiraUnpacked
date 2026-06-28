using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Security;
using JiraUnpacked.Infrastructure.Identity;
using JiraUnpacked.Infrastructure.Messaging;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Security;
using JiraUnpacked.Infrastructure.Services;
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

        return services;
    }
}
