namespace ThanyMarcus.Portal.SagaWorker;

public sealed partial class SagaWorker(ILogger<SagaWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                LogTick(logger);
            }
        }
        catch (OperationCanceledException)
        {
            LogStopping(logger);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "SagaWorker started (scaffold; no saga logic yet — see PORTAL-007)")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Trace, Message = "SagaWorker tick (no-op)")]
    private static partial void LogTick(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "SagaWorker stopping")]
    private static partial void LogStopping(ILogger logger);
}
