using System.Diagnostics;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Embedding;

public sealed partial class GraniteEmbeddingWarmupService : IHostedService
{
    private readonly IEmbeddingClient client;
    private readonly ILogger<GraniteEmbeddingWarmupService> log;

    public GraniteEmbeddingWarmupService(IEmbeddingClient client, ILogger<GraniteEmbeddingWarmupService> log)
    {
        this.client = client;
        this.log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            _ = await client.EmbedAsync("warmup", cancellationToken).ConfigureAwait(false);
            LogCompleted(log, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(log, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Granite embedding warmup completed in {Ms} ms")]
    private static partial void LogCompleted(ILogger logger, long ms);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Granite embedding warmup failed; first capture will load lazily")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
