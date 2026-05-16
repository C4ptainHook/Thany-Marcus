namespace ThanyMarcus.Portal.Api.Features.Auth.Lockout;

public static class LockoutServiceCollectionExtensions
{
    public static IServiceCollection AddAuthLockout(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddOptions<LockoutOptions>()
                .Bind(cfg.GetSection("Lockout"));
        services.AddScoped<AuthLockoutService>();
        services.AddScoped<LockoutGuardFilter>();
        return services;
    }
}
