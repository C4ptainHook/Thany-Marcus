using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ThanyMarcus.Portal.Api.Features.Auth.RateLimiting;

public static class CloudCallbackPolicies
{
    public const string PerCloudId = "cloud_callback_per_id";
    public const string PerIp      = "cloud_callback_per_ip";

    public static void Configure(RateLimiterOptions options)
    {
        options.AddPolicy(PerCloudId, ctx =>
        {
            var id = ctx.Request.RouteValues["cloudId"]?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(id, _ =>
                new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 12,
                    Window = TimeSpan.FromMinutes(5),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
        });
        options.AddPolicy(PerIp, ctx =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(ip, _ =>
                new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(5),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
        });
    }
}
