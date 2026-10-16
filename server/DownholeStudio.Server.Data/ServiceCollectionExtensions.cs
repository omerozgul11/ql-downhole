using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DownholeStudio.Server.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the database, licensing services and options. Secrets are read from configuration,
    /// which includes environment variables: ConnectionStrings__Licensing, LICENSING_SIGNING_KEY_PEM,
    /// LICENSING_KEY_PEPPER, UPDATES_SIGNING_KEY_PEM, Stripe__WebhookSecret, Smtp__Password.
    /// </summary>
    public static IServiceCollection AddLicensingData(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Licensing")
            ?? throw new InvalidOperationException("ConnectionStrings:Licensing (env ConnectionStrings__Licensing) is not configured.");
        services.AddDbContext<LicensingDbContext>(o => o.UseNpgsql(connection, npgsql => npgsql.EnableRetryOnFailure(3)));
        services.TryAddSingleton(TimeProvider.System);

        services.Configure<ServerLicensingOptions>(configuration.GetSection(ServerLicensingOptions.Section));
        services.PostConfigure<ServerLicensingOptions>(o =>
        {
            o.SigningKeyPem ??= configuration["LICENSING_SIGNING_KEY_PEM"];
            o.KeyPepper ??= configuration["LICENSING_KEY_PEPPER"];
        });
        services.Configure<UpdateSigningOptions>(configuration.GetSection(UpdateSigningOptions.Section));
        services.PostConfigure<UpdateSigningOptions>(o => o.SigningKeyPem ??= configuration["UPDATES_SIGNING_KEY_PEM"]);
        services.Configure<StripeOptions>(configuration.GetSection(StripeOptions.Section));
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.Section));
        services.Configure<EmailContentOptions>(configuration.GetSection(EmailContentOptions.Section));

        services.AddSingleton(sp => new TokenIssuer(sp.GetRequiredService<IOptions<ServerLicensingOptions>>().Value));
        services.AddSingleton(sp => new UpdateSigner(sp.GetRequiredService<IOptions<UpdateSigningOptions>>().Value));
        services.AddSingleton<EmailTemplates>();
        services.TryAddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<LicenseKeyHasher>();
        services.AddScoped<EmailOutbox>();
        services.AddScoped<AuditService>();
        services.AddScoped<LicenseService>();
        services.AddScoped<AdminService>();
        services.AddScoped<AdminAccountService>();
        services.AddScoped<UpdateService>();
        services.AddScoped<StripePaymentProvider>();
        services.AddScoped<IPaymentProvider>(sp => sp.GetRequiredService<StripePaymentProvider>());
        return services;
    }
}
