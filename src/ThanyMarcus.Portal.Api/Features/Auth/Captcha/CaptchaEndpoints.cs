using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ThanyMarcus.Portal.Api.Features.Auth.Lockout;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.Captcha;

public static class CaptchaEndpoints
{
    public static void MapCaptchaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/captcha-state", async (
            string kind,
            HttpContext http,
            CaptchaRequirementTracker tracker,
            PortalDbContext db,
            IOptions<TurnstileOptions> options,
            CancellationToken ct) =>
        {
            var cfg = options.Value;
            if (!cfg.IsEnabled) return Results.Ok(new CaptchaStateResponse(false, ""));

            string partitionKey;
            string? failureKind;
            switch (kind)
            {
                case TurnstileKinds.Signup:
                    // Anonymous signup always requires a token when Turnstile is configured.
                    return Results.Ok(new CaptchaStateResponse(true, cfg.SiteKey));
                case TurnstileKinds.Signin:
                    partitionKey = "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                    failureKind = null;
                    break;
                case AuthLockoutKinds.Totp:
                case AuthLockoutKinds.Unlock:
                    if (http.User.Identity?.IsAuthenticated != true)
                        return Results.Unauthorized();
                    var sub = http.User.FindFirstValue(AuthClaimTypes.SubUs);
                    if (string.IsNullOrEmpty(sub)) return Results.Unauthorized();
                    partitionKey = "u:" + sub;
                    failureKind = kind;
                    break;
                default:
                    return Results.BadRequest(new { error = "invalid_kind" });
            }

            if (tracker.IsRequired(partitionKey))
                return Results.Ok(new CaptchaStateResponse(true, cfg.SiteKey));

            if (failureKind is not null
                && Guid.TryParse(partitionKey.AsSpan(2), out var userId))
            {
                var count = await db.AuthLockouts
                    .AsNoTracking()
                    .Where(a => a.UserId == userId && a.Kind == failureKind)
                    .Select(a => (short?)a.FailedCount)
                    .SingleOrDefaultAsync(ct);
                if (count is not null && count.Value >= cfg.FailureThreshold)
                    return Results.Ok(new CaptchaStateResponse(true, cfg.SiteKey));
            }

            return Results.Ok(new CaptchaStateResponse(false, cfg.SiteKey));
        });
    }
}

public sealed record CaptchaStateResponse(
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("site_key")] string SiteKey);
