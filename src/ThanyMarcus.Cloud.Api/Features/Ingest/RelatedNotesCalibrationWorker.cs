using Microsoft.Extensions.Options;
using NodaTime;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public sealed partial class RelatedNotesCalibrationWorker : BackgroundService
{
    private readonly RelatedNotesCalibrationSignal signal;
    private readonly IServiceProvider services;
    private readonly IOptions<RelatedNotesOptions> opts;
    private readonly IClock clock;
    private readonly ILogger<RelatedNotesCalibrationWorker> log;

    private Instant? lastCheck;

    public RelatedNotesCalibrationWorker(
        RelatedNotesCalibrationSignal signal,
        IServiceProvider services,
        IOptions<RelatedNotesOptions> opts,
        IClock clock,
        ILogger<RelatedNotesCalibrationWorker> log)
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

            var cooldown = Duration.FromSeconds(opts.Value.AutoCheckCooldownSeconds);
            var now = clock.GetCurrentInstant();
            if (lastCheck is { } prev && now - prev < cooldown) continue;
            lastCheck = now;

            try
            {
                await using var scope = services.CreateAsyncScope();
                var calibrator = scope.ServiceProvider.GetRequiredService<RelatedNotesCalibrator>();
                await calibrator.RunAsync(stoppingToken);
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Related-notes Auto recalibration failed")]
    private static partial void LogFailure(ILogger logger, Exception ex);
}
