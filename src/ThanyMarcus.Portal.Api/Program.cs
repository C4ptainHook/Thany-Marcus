using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NodaTime;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.Lockout;
using ThanyMarcus.Portal.Api.Features.Auth.RateLimiting;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Auth.Totp;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
});

builder.Services.AddOpenApi();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: "ThanyMarcus.Portal.Api",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddProcessInstrumentation()
        .AddPrometheusExporter())
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

builder.Services.AddSingleton<IClock>(NodaTime.SystemClock.Instance);
builder.Services.AddSingleton<TimestampInterceptor>();
builder.Services.AddDbContext<PortalDbContext>((sp, opts) => opts
    .UseNpgsql(
        builder.Configuration.GetConnectionString("Portal")
            ?? throw new InvalidOperationException("ConnectionStrings:Portal not configured"),
        npg => npg.UseNodaTime())
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));

builder.Services.AddScoped<GoogleSignInHandler>();
builder.Services.AddScoped<CookiePrincipalValidator>();

// TODO(PORTAL-017): PersistKeysToFileSystem so restarts don't invalidate TotpSecret ciphertexts.
builder.Services.AddDataProtection()
    .SetApplicationName("ThanyMarcus.Portal");
builder.Services.AddScoped<TotpService>();
builder.Services.AddScoped<TotpBackupCodeService>();

builder.Services.AddScoped<PassphraseService>();
builder.Services.AddSingleton<InProcessInfraOpUnlockCache>();
builder.Services.AddSingleton<IInfraOpUnlockCache>(sp => sp.GetRequiredService<InProcessInfraOpUnlockCache>());
builder.Services.AddHostedService<InfraOpUnlockSweepService>();

builder.Services.AddAuthentication(opts =>
{
    opts.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    opts.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
})
.AddCookie(opts =>
{
    opts.Cookie.Name = ".Portal.Auth";
    opts.ExpireTimeSpan = TimeSpan.FromDays(14);
    opts.SlidingExpiration = true;
    opts.Cookie.HttpOnly = true;
    opts.Cookie.SameSite = SameSiteMode.Lax;
    opts.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    opts.LoginPath = "/api/auth/signin";
    opts.LogoutPath = "/api/auth/signout";
    opts.AccessDeniedPath = "/totp-challenge";
    opts.Events.OnValidatePrincipal = async ctx =>
    {
        var validator = ctx.HttpContext.RequestServices.GetRequiredService<CookiePrincipalValidator>();
        var outcome = await validator.ValidateAsync(
            ctx.Principal!,
            ctx.Properties.IssuedUtc,
            ctx.HttpContext.RequestAborted);
        if (outcome == CookieValidationOutcome.Reject)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
        else
        {
            ctx.ShouldRenew = true;
        }
    };
    opts.Events.OnRedirectToLogin = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
    opts.Events.OnRedirectToAccessDenied = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
})
.AddGoogle(opts =>
{
    opts.ClientId = builder.Configuration["Google:ClientId"]
        ?? throw new InvalidOperationException("Google:ClientId not configured");
    opts.ClientSecret = builder.Configuration["Google:ClientSecret"]
        ?? throw new InvalidOperationException("Google:ClientSecret not configured");
    opts.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    opts.Scope.Add("email");
    opts.Scope.Add("profile");
    opts.SaveTokens = false;
    opts.Events.OnCreatingTicket = async ctx =>
    {
        var handler = ctx.HttpContext.RequestServices.GetRequiredService<GoogleSignInHandler>();
        var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
        await handler.HandleAsync(identity, ctx.User, ctx.HttpContext.RequestAborted);
    };
});

builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy(AuthPolicies.TotpRequired, p => p.RequireAssertion(c =>
        c.User.FindFirstValue(AuthClaimTypes.Totp)
            is TotpClaimValues.Verified or TotpClaimValues.NotEnabled));
});

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live", "ready"])
    .AddDbContextCheck<PortalDbContext>(tags: ["ready"]);

builder.Services.AddAuthRateLimiting(builder.Configuration);
builder.Services.AddAuthLockout(builder.Configuration);
builder.Services.AddHostedService<AuthLockoutSweepService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
    await db.Database.MigrateAsync();
}

app.UseStaticFiles();

app.UseMiddleware<SignInGoogleRateLimitMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapPrometheusScrapingEndpoint();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

app.MapAuthEndpoints();
app.MapTotpEndpoints();
app.MapPassphraseEndpoints();

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
