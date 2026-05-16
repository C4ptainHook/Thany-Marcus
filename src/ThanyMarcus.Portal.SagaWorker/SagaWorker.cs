using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

namespace ThanyMarcus.Portal.SagaWorker;

public sealed partial class SagaWorker : BackgroundService
{
    private readonly IServiceProvider services;
    private readonly ILogger<SagaWorker> log;
    private readonly SemaphoreSlim concurrency;
    private readonly string workerId;
    private readonly Duration leaseDuration;
    private readonly TimeSpan idleDelay;

    public SagaWorker(
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment env,
        ILogger<SagaWorker> log)
    {
        this.services = services;
        this.log = log;
        var max = config.GetValue("Provisioning:MaxConcurrentJobs", 3);
        concurrency = new SemaphoreSlim(max, max);
        leaseDuration = Duration.FromSeconds(config.GetValue("Provisioning:LeaseSeconds", 120));
        idleDelay = TimeSpan.FromMilliseconds(config.GetValue("Provisioning:IdlePollMs", 500));
        workerId = $"{env.ApplicationName}@{Environment.MachineName}/{Guid.NewGuid().ToString("N")[..8]}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(log, workerId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await concurrency.WaitAsync(stoppingToken);

                ProvisioningJob? job;
                try
                {
                    job = await ClaimNextAsync(stoppingToken);
                }
                catch
                {
                    concurrency.Release();
                    throw;
                }

                if (job is null)
                {
                    concurrency.Release();
                    try { await Task.Delay(idleDelay, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await using var scope = services.CreateAsyncScope();
                        var dispatcher = scope.ServiceProvider.GetRequiredService<SagaPhaseDispatcher>();
                        await dispatcher.HandleAsync(job, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        LogDispatchFailure(log, ex, job.Id);
                    }
                    finally
                    {
                        concurrency.Release();
                    }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        LogStopping(log);
    }

    private async Task<ProvisioningJob?> ClaimNextAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();

        var sql = $$"""
            UPDATE provisioning_jobs
            SET claimed_by       = {0},
                lease_expires_at = now() + (interval '1 second' * {1}),
                next_visible_at  = now() + (interval '1 second' * {1}),
                attempt_count    = attempt_count + 1,
                updated_at       = now()
            WHERE id = (
              SELECT id FROM provisioning_jobs
              WHERE status NOT IN ('succeeded','failed_tf','failed_dns','failed_callback','failed_cert','failed_destroy','cancelled','rolled_back')
                AND next_visible_at <= now()
                AND (claimed_by IS NULL OR lease_expires_at <= now())
              ORDER BY next_visible_at
              LIMIT 1
              FOR UPDATE SKIP LOCKED
            )
            RETURNING *
            """;

        var rows = await db.ProvisioningJobs
            .FromSqlRaw(sql, workerId, (int)leaseDuration.TotalSeconds)
            .AsNoTracking()
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "SagaWorker started; worker_id={WorkerId}")]
    private static partial void LogStarted(ILogger logger, string workerId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "SagaWorker stopping")]
    private static partial void LogStopping(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "SagaWorker dispatch failed for job {JobId}")]
    private static partial void LogDispatchFailure(ILogger logger, Exception ex, Guid jobId);
}
