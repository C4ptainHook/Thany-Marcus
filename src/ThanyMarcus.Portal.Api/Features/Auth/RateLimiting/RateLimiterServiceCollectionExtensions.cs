using Microsoft.AspNetCore.RateLimiting;

namespace ThanyMarcus.Portal.Api.Features.Auth.RateLimiting;

public static class RateLimiterServiceCollectionExtensions
{
    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddOptions<RateLimitingOptions>().Bind(cfg.GetSection("RateLimiting"));
        services.AddRateLimiter(opts =>
        {
            var snapshot = cfg.GetSection("RateLimiting").Get<RateLimitingOptions>() ?? new RateLimitingOptions();
            AuthRateLimiterPolicies.Configure(opts, snapshot);
            CloudCallbackPolicies.Configure(opts);
        });
        services.AddSingleton<SignInGoogleRateLimitMiddleware>();
        return services;
    }
}
