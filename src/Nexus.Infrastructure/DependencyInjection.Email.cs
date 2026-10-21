using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Application.Email;
using Nexus.Infrastructure.Email;

namespace Nexus.Infrastructure;

/// <summary>
/// Stage D's wiring, in its own file so four parallel streams do not fight over one
/// method. <c>DependencyInjection.AddNexusInfrastructure</c> calls this; nothing here edits
/// that file.
/// </summary>
public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddNexusEmail(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<EmailOptions>(config.GetSection(EmailOptions.Section));
        services.Configure<SiteOptions>(config.GetSection(SiteOptions.Section));

        // The dispatcher and the retry policy read the clock; a test replaces it rather
        // than sleeping through an eighteen-hour retry schedule.
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<SuppressionCircuitBreaker>();

        services.AddScoped<EmailIdentity>();
        services.AddScoped<EmailSuppressionList>();
        services.AddScoped<IEmailSuppressionList>(sp => sp.GetRequiredService<EmailSuppressionList>());
        services.AddScoped<IEmailOutbox, EmailOutbox>();
        services.AddScoped<IEmailTemplateRenderer, EmailTemplateRenderer>();
        services.AddScoped<OrderEmailService>();
        services.AddScoped<IOrderEmailService>(sp => sp.GetRequiredService<OrderEmailService>());
        services.AddScoped<IAdminEmailOperations>(sp => sp.GetRequiredService<OrderEmailService>());
        services.AddScoped<EmailRetryPolicy>();
        services.AddScoped<OutboxPump>();
        services.AddScoped<EmailTemplateSeeder>();
        services.AddScoped<EmailMaintenance>();

        /* Selected by configuration, not by IHostEnvironment. The environment only supplies
         * the default: staging needs the file drop before an SMTP account exists, and
         * production needs None on day one, because the provider is still an open question
         * and shipping with email off beats shipping with email broken. */
        services.AddScoped<IEmailSender>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>();
            return options.Value.EffectiveTransport() switch
            {
                EmailTransport.Smtp => ActivatorUtilities.CreateInstance<SmtpEmailSender>(sp),
                EmailTransport.FileDrop => ActivatorUtilities.CreateInstance<FileDropEmailSender>(sp),
                _ => new NullEmailSender()
            };
        });

        services.AddHostedService<EmailDispatcher>();

        // Registered as a service as well as a check so it can be exercised directly;
        // AddCheck alone constructs it through ActivatorUtilities and nothing can resolve it.
        services.AddScoped<OutboxHealthCheck>();
        services.AddHealthChecks().AddCheck<OutboxHealthCheck>("outbox");

        return services;
    }
}
