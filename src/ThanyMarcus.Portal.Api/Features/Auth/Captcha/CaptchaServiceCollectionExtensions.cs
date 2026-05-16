using Microsoft.Extensions.Options;

namespace ThanyMarcus.Portal.Api.Features.Auth.Captcha;

public static partial class CaptchaServiceCollectionExtensions
{
    public static IServiceCollection AddTurnstile(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddOptions<TurnstileOptions>().Bind(cfg.GetSection("Turnstile"));
        services.AddSingleton<CaptchaRequirementTracker>();
        services.AddHttpClient<ITurnstileValidator, TurnstileValidator>();
        services.AddScoped<RequireTurnstileFilter>();
        services.AddSingleton<IStartupFilter, TurnstileStartupLogger>();
        return services;
    }

    private sealed partial class TurnstileStartupLogger(
        IOptions<TurnstileOptions> options,
        ILogger<TurnstileStartupLogger> logger) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            if (!options.Value.IsEnabled)
                LogDisabled(logger);
            return next;
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
            Message = "Turnstile is disabled (SiteKey not configured); captcha enforcement skipped.")]
        private static partial void LogDisabled(ILogger logger);
    }
}
