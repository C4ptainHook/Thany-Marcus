using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ThanyMarcus.Portal.Api.Features.Auth.RateLimiting;

public static class AuthRateLimiterPolicies
{
    public const string TotpChallenge = "auth-totp-challenge";
    public const string Unlock = "auth-unlock";
    public const string SignInGoogle = "signin-google";

    public static void Configure(RateLimiterOptions opts, RateLimitingOptions cfg)
    {
        opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        opts.OnRejected = OnRejectedAsync;

        opts.AddPolicy(TotpChallenge, ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: PartitionKeyForUser(ctx),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = cfg.TotpChallenge.PermitLimit,
                    Window = TimeSpan.FromSeconds(cfg.TotpChallenge.WindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

        opts.AddPolicy(Unlock, ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: PartitionKeyForUser(ctx),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = cfg.Unlock.PermitLimit,
                    Window = TimeSpan.FromSeconds(cfg.Unlock.WindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
    }

    private static string PartitionKeyForUser(HttpContext ctx)
    {
        var sub = ctx.User.FindFirstValue(AuthClaimTypes.SubUs);
        if (!string.IsNullOrEmpty(sub)) return "u:" + sub;
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return "ip:" + ip;
    }

    private static async ValueTask OnRejectedAsync(OnRejectedContext ctx, CancellationToken ct)
    {
        var retryAfter = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)
            ? (int)Math.Ceiling(retry.TotalSeconds)
            : 0;
        if (retryAfter > 0)
            ctx.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.HttpContext.Response.WriteAsJsonAsync(
            new RateLimitErrorBody("rate_limited", retryAfter), ct);
    }
}
