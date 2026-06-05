using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;

namespace ThanyMarcus.Cloud.Api.Features.Sync;

public sealed partial class InboxRerouteWorker : BackgroundService
{
    private readonly InboxRerouteSignal signal;
    private readonly IServiceProvider services;
    private readonly IOptionsMonitor<LlmIntelligenceOptions> opts;
    private readonly IClock clock;
    private readonly ILogger<InboxRerouteWorker> log;

    private Instant? lastRun;

    public InboxRerouteWorker(
        InboxRerouteSignal signal,
        IServiceProvider services,
        IOptionsMonitor<LlmIntelligenceOptions> opts,
        IClock clock,
        ILogger<InboxRerouteWorker> log)
    {
        this.signal = signal;
        this.services = services;
        this.opts = opts;
        this.clock = clock;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await signal.Reader.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var cooldown = Duration.FromSeconds(opts.CurrentValue.RerouteCooldownSeconds);
            var now = clock.GetCurrentInstant();
            if (lastRun is { } prev && now - prev < cooldown) continue;
            lastRun = now;

            try
            {
                await using var scope = services.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<InboxRerouteService>();
                var result = await service.RunAsync(stoppingToken);
                if (result.AffectedCount > 0)
                {
                    var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
                    await SyncMaintenanceEndpoints.NotifyAsync(
                        db, JobOrchestratorWorker.ChangedChannel, "inbox_reroute", stoppingToken);
                    LogRerouted(log, result.AffectedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogFailure(log, ex);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Inbox cold-start auto-heal re-routed {Count} note(s)")]
    private static partial void LogRerouted(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Inbox re-route worker run failed")]
    private static partial void LogFailure(ILogger logger, Exception ex);
}
