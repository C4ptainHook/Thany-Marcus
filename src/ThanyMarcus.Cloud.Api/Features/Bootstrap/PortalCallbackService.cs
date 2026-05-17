using System.Net.Http.Json;
using NodaTime;
using ThanyMarcus.Shared.CloudAdmin;

namespace ThanyMarcus.Cloud.Api.Features.Bootstrap;

public sealed partial class PortalCallbackService(
    IHttpClientFactory httpFactory,
    BootstrapOptions opts,
    BootstrapState state,
    IClock clock,
    ILogger<PortalCallbackService> log)
{
    internal const int MaxAttempts = 8;
    public const string HttpClientName = "portal-callback";

    public async Task PostRegistrationAsync(CancellationToken ct)
    {
        if (!state.TryBeginRegistration())
        {
            LogDuplicateInFlight(log);
            return;
        }

        try
        {
            if (state.RegistrationStatus == CloudAdminHealthResponse.RegistrationRegistered)
            {
                return;
            }

            using var http = httpFactory.CreateClient(HttpClientName);

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                try
                {
                    using var resp = await http.PostAsJsonAsync(
                        opts.PortalCallbackUrl,
                        new CallbackRequest(opts.CloudId, opts.EnrollmentToken, opts.CloudAdminToken),
                        ct).ConfigureAwait(false);

                    if (resp.IsSuccessStatusCode)
                    {
                        state.RegistrationStatus = CloudAdminHealthResponse.RegistrationRegistered;
                        state.RegisteredAt = clock.GetCurrentInstant();
                        LogRegistered(log, attempt + 1);
                        return;
                    }

                    if ((int)resp.StatusCode is >= 400 and < 500)
                    {
                        LogPermanentFailure(log, (int)resp.StatusCode);
                        state.RegistrationStatus = CloudAdminHealthResponse.RegistrationFailed;
                        return;
                    }

                    LogTransientFailure(log, (int)resp.StatusCode, attempt + 1);
                }
                catch (HttpRequestException ex)
                {
                    LogNetworkFailure(log, ex, attempt + 1);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    LogTimeout(log, attempt + 1);
                }

                if (ct.IsCancellationRequested) return;
                if (attempt == MaxAttempts - 1) break;

                var delaySeconds = Math.Pow(2, attempt);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }

            state.RegistrationStatus = CloudAdminHealthResponse.RegistrationFailed;
            LogExhausted(log, MaxAttempts);
        }
        finally
        {
            state.EndRegistration();
        }
    }

    private sealed record CallbackRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("cloud_id")] Guid CloudId,
        [property: System.Text.Json.Serialization.JsonPropertyName("enrollment_token")] string EnrollmentToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("cloud_admin_token")] string CloudAdminToken);

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "PortalCallbackService: callback already in flight; skipping duplicate")]
    private static partial void LogDuplicateInFlight(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "PortalCallbackService: registered with portal on attempt {Attempt}")]
    private static partial void LogRegistered(ILogger logger, int attempt);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "PortalCallbackService: portal rejected callback with status {Status}; not retrying")]
    private static partial void LogPermanentFailure(ILogger logger, int status);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "PortalCallbackService: transient status {Status} on attempt {Attempt}")]
    private static partial void LogTransientFailure(ILogger logger, int status, int attempt);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "PortalCallbackService: network failure on attempt {Attempt}")]
    private static partial void LogNetworkFailure(ILogger logger, Exception ex, int attempt);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "PortalCallbackService: timeout on attempt {Attempt}")]
    private static partial void LogTimeout(ILogger logger, int attempt);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "PortalCallbackService: registration failed after {Max} attempts")]
    private static partial void LogExhausted(ILogger logger, int max);
}
