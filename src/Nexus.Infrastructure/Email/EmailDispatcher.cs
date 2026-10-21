using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Nexus.Infrastructure.Email;

/// <summary>
/// The only thing in the system that talks to a mail server.
/// <para>
/// Nothing sends inside a web request. Tap expects a prompt 200 from the webhook, a TLS
/// handshake to a mail host is 200 ms on a good day and a 30-second timeout on a bad one,
/// and an SMTP timeout must never be the reason a paid order fails to complete.
/// </para>
/// <para>
/// <b>Known operational hazard:</b> IIS stops an idle worker process after twenty minutes
/// by default, and this goes with it. The app pool must be Always Running, the health check
/// goes Unhealthy when the oldest queued row passes fifteen minutes, and the nightly sweep
/// reports the same four sets. Deliberately not mitigated with a public keep-alive
/// endpoint — that is an unauthenticated route on a public repository, and a cron ping is
/// the operator's job, not the application's.
/// </para>
/// </summary>
public sealed partial class EmailDispatcher(
    IServiceScopeFactory scopes,
    IOptions<EmailOptions> options,
    ILogger<EmailDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var idle = TimeSpan.FromSeconds(Math.Clamp(options.Value.DispatchIntervalSeconds, 1, 300));
        LogStarted(log, options.Value.EffectiveTransport(), (int)idle.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;

            try
            {
                using var scope = scopes.CreateScope();
                var pump = scope.ServiceProvider.GetRequiredService<OutboxPump>();
                processed = await pump.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The dispatcher stopping is the failure this whole phase is designed
                // against, so no exception from one pass is allowed to end the loop.
                LogPassFailed(log, ex);
            }

            // A full batch means there is a backlog; go straight round again rather than
            // make a customer who is waiting on a link wait another fifteen seconds.
            if (processed < options.Value.BatchSize)
                await Task.Delay(idle, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 5160, Level = LogLevel.Information,
        Message = "Email dispatcher started: transport {Transport}, idle interval {Seconds}s.")]
    private static partial void LogStarted(ILogger logger, EmailTransport transport, int seconds);

    [LoggerMessage(EventId = 5161, Level = LogLevel.Error, Message = "An outbox pass failed; the dispatcher continues.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
