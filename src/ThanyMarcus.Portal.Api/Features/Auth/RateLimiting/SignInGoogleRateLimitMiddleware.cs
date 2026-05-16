using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.Captcha;

namespace ThanyMarcus.Portal.Api.Features.Auth.RateLimiting;

public sealed class SignInGoogleRateLimitMiddleware : IMiddleware, IAsyncDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> _limiter;
    private readonly CaptchaRequirementTracker _tracker;
    private readonly IOptions<TurnstileOptions> _turnstileOptions;

    public SignInGoogleRateLimitMiddleware(
        IOptions<RateLimitingOptions> opts,
        CaptchaRequirementTracker tracker,
        IOptions<TurnstileOptions> turnstileOptions)
    {
        _tracker = tracker;
        _turnstileOptions = turnstileOptions;
        var cfg = opts.Value.SignInGoogle;
        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = cfg.PermitLimit,
                    Window = TimeSpan.FromSeconds(cfg.WindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.Equals("/signin-google", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        using var lease = await _limiter.AcquireAsync(context, permitCount: 1, context.RequestAborted);
        if (!lease.IsAcquired)
        {
            var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)
                ? (int)Math.Ceiling(retry.TotalSeconds)
                : 0;

            if (_turnstileOptions.Value.IsEnabled)
            {
                var key = "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                _tracker.MarkRequired(key, Duration.FromSeconds(_turnstileOptions.Value.TrackerTtlSeconds));
            }

            if (retryAfter > 0)
                context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(
                new RateLimitErrorBody("rate_limited", retryAfter), context.RequestAborted);
            return;
        }

        await next(context);
    }

    public async ValueTask DisposeAsync() => await _limiter.DisposeAsync();
}
