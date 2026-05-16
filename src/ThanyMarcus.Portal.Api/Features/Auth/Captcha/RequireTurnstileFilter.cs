using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.Captcha;

public sealed class RequireTurnstileFilter(
    CaptchaRequirementTracker tracker,
    ITurnstileValidator validator,
    PortalDbContext db,
    IOptions<TurnstileOptions> options) : IEndpointFilter
{
    private const string TokenHeader = "cf-turnstile-response";
    private const string TokenQueryParam = "turnstile";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var cfg = options.Value;
        if (!cfg.IsEnabled) return await next(context);

        var http = context.HttpContext;
        var metadata = http.GetEndpoint()?.Metadata.GetMetadata<TurnstileKindMetadata>();
        if (metadata is null) return await next(context);

        var (partitionKey, failureKind) = ResolvePartition(http, metadata.Kind);
        if (partitionKey is null) return await next(context);

        var required = await IsCaptchaRequiredAsync(partitionKey, failureKind, cfg.FailureThreshold, http.RequestAborted);
        if (!required) return await next(context);

        var token = http.Request.Headers[TokenHeader].ToString();
        if (string.IsNullOrEmpty(token))
            token = http.Request.Query[TokenQueryParam].ToString();

        if (string.IsNullOrEmpty(token))
            return CaptchaRequiredResponse(cfg.SiteKey);

        var verify = await validator.VerifyAsync(token, http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted);
        if (!verify.Success)
            return CaptchaRequiredResponse(cfg.SiteKey, verify.ErrorCodes);

        tracker.Clear(partitionKey);
        return await next(context);
    }

    private static (string? partitionKey, string? failureKind) ResolvePartition(HttpContext http, string kind)
    {
        if (string.Equals(kind, TurnstileKinds.Signin, StringComparison.Ordinal))
            return ("ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"), null);

        var sub = http.User.FindFirstValue(AuthClaimTypes.SubUs);
        if (string.IsNullOrEmpty(sub)) return (null, null);
        return ("u:" + sub, kind);
    }

    private async Task<bool> IsCaptchaRequiredAsync(
        string partitionKey, string? failureKind, int threshold, CancellationToken ct)
    {
        if (tracker.IsRequired(partitionKey)) return true;
        if (failureKind is null) return false;
        if (!partitionKey.StartsWith("u:", StringComparison.Ordinal)) return false;
        if (!Guid.TryParse(partitionKey.AsSpan(2), out var userId)) return false;

        var count = await db.AuthLockouts
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.Kind == failureKind)
            .Select(a => (short?)a.FailedCount)
            .SingleOrDefaultAsync(ct);
        return count is not null && count.Value >= threshold;
    }

    private static IResult CaptchaRequiredResponse(string siteKey, IReadOnlyList<string>? errorCodes = null)
        => Results.Json(
            new CaptchaRequiredBody("captcha_required", siteKey, errorCodes),
            statusCode: StatusCodes.Status428PreconditionRequired);

    private sealed record CaptchaRequiredBody(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("site_key")] string SiteKey,
        [property: JsonPropertyName("error_codes")] IReadOnlyList<string>? ErrorCodes);
}
