using System.Globalization;
using System.Security.Claims;

namespace ThanyMarcus.Portal.Api.Features.Auth.Lockout;

public sealed class LockoutGuardFilter(AuthLockoutService lockouts) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var subClaim = http.User.FindFirstValue(AuthClaimTypes.SubUs);
        if (string.IsNullOrEmpty(subClaim)) return Results.Unauthorized();
        if (!Guid.TryParse(subClaim, out var userId)) return Results.Unauthorized();

        var endpoint = http.GetEndpoint();
        var kind = endpoint?.Metadata.GetMetadata<LockoutKindMetadata>()?.Kind;
        if (kind is null) return await next(context);

        var state = await lockouts.IsLockedAsync(userId, kind, http.RequestAborted);
        if (!state.IsLocked) return await next(context);

        http.Response.Headers.RetryAfter = state.RemainingSeconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(
            new
            {
                error             = "account_locked",
                locked_until      = state.LockedUntil!.Value.ToString("g", CultureInfo.InvariantCulture),
                remaining_seconds = state.RemainingSeconds,
            },
            statusCode: StatusCodes.Status423Locked);
    }
}

public sealed record LockoutKindMetadata(string Kind);
