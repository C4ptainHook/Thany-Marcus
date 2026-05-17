using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;

namespace ThanyMarcus.Cloud.Api.Features.Bootstrap;

public static partial class CertInstalledEndpoint
{
    public static void MapCertInstalledEndpoint(this IEndpointRouteBuilder app) =>
        app.MapPost("/internal/cert-installed", (
            CertInstalledPayload body,
            PortalCallbackService callback,
            BootstrapOptions opts,
            IHostApplicationLifetime lifetime,
            ILogger<CertInstalledPayloadLog> log) =>
        {
            LogReceived(log, body.Identifier ?? "(none)");

            if (!string.Equals(body.Identifier, opts.Hostname, StringComparison.OrdinalIgnoreCase))
            {
                return Results.NoContent();
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await callback.PostRegistrationAsync(lifetime.ApplicationStopping).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogBackgroundFailure(log, ex);
                }
            }, lifetime.ApplicationStopping);

            return Results.NoContent();
        })
        .WithName("CertInstalled")
        .AllowAnonymous();

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "CertInstalled: received event for {Identifier}")]
    private static partial void LogReceived(ILogger logger, string identifier);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "CertInstalled: background callback dispatch failed")]
    private static partial void LogBackgroundFailure(ILogger logger, Exception ex);

    internal sealed class CertInstalledPayloadLog;
}

public sealed record CertInstalledPayload(
    [property: JsonPropertyName("event")] string? Event,
    [property: JsonPropertyName("identifier")] string? Identifier);
