using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;

namespace ThanyMarcus.Cloud.Api.Features.Bootstrap;

public static partial class CaddyEventsEndpoint
{
    public const string CertObtainedEvent = "cert_obtained";

    public static void MapCaddyEventsEndpoint(this IEndpointRouteBuilder app) =>
        app.MapPost("/internal/caddy-events", (
            CaddyEventPayload body,
            PortalCallbackService callback,
            BootstrapOptions opts,
            IHostApplicationLifetime lifetime,
            ILogger<CaddyEventPayloadLog> log) =>
        {
            LogReceived(log, body.Event, body.Identifier ?? "(none)");

            if (body.Event != CertObtainedEvent ||
                !string.Equals(body.Identifier, opts.Hostname, StringComparison.OrdinalIgnoreCase))
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
        .WithName("CaddyEvents")
        .AllowAnonymous();

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "CaddyEvents: received {Event} for {Identifier}")]
    private static partial void LogReceived(ILogger logger, string @event, string identifier);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "CaddyEvents: background callback dispatch failed")]
    private static partial void LogBackgroundFailure(ILogger logger, Exception ex);

    internal sealed class CaddyEventPayloadLog;
}

public sealed record CaddyEventPayload(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("identifier")] string? Identifier);
