using Microsoft.Extensions.Hosting;
using ThanyMarcus.Cloud.Api.Features.Admin.Health;
using ThanyMarcus.Shared.CloudAdmin;

namespace ThanyMarcus.Cloud.Api.Features.Bootstrap;

public sealed partial class RegistrationStartupSweeper(
    CertFileReader certs,
    BootstrapState state,
    PortalCallbackService callback,
    ILogger<RegistrationStartupSweeper> log) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false); }
        catch (TaskCanceledException) { return; }

        var observed = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (state.RegistrationStatus == CloudAdminHealthResponse.RegistrationRegistered)
                return;

            if (certs.IsCertReady())
            {
                if (!observed) { LogObserved(log); observed = true; }
                await callback.PostRegistrationAsync(stoppingToken).ConfigureAwait(false);
                if (state.RegistrationStatus == CloudAdminHealthResponse.RegistrationRegistered)
                    return;
            }

            try { await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "RegistrationStartupSweeper: cert observed on disk; ensuring portal registration")]
    private static partial void LogObserved(ILogger logger);
}
