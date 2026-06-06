using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Fido2NetLib;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Polly;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.Captcha;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.Auth.Login;
using ThanyMarcus.Portal.Api.Features.Auth.Lockout;
using ThanyMarcus.Portal.Api.Features.Auth.Passkey;
using ThanyMarcus.Portal.Api.Features.Auth.RateLimiting;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Auth.Totp;
using ThanyMarcus.Portal.Api.Features.Admin;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Callback;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Cancel;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Create;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Destroy;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Migrate;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Events;
using ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens;
using ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens.Sync;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderMeta;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Status;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.Provisioning.Pricing;
using ThanyMarcus.Portal.Api.Features.Provisioning.Providers;
using ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;
using ThanyMarcus.Portal.Api.Features.Releases;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Shared.Database;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
});

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb));

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

builder.Services.Configure<PasskeyConfiguration>(builder.Configuration.GetSection("Auth:Passkey"));
var passkeyConfig = builder.Configuration.GetSection("Auth:Passkey").Get<PasskeyConfiguration>()
    ?? new PasskeyConfiguration();
builder.Services.AddFido2(opts =>
{
    opts.ServerDomain = passkeyConfig.ServerDomain;
    opts.ServerName = passkeyConfig.ServerName;
    opts.Origins = new HashSet<string>(passkeyConfig.Origins);
    opts.TimestampDriftTolerance = 300_000;
});
builder.Services.AddSingleton<IPasskeyChallengeStore, PasskeyChallengeStore>();
builder.Services.AddHostedService<PasskeyChallengeStoreSweeper>();
builder.Services.AddScoped<PasskeySignInHandler>();

var dpKeysDir = builder.Configuration["DataProtection:KeyRingPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data-protection-keys");
Directory.CreateDirectory(dpKeysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir))
    .SetApplicationName("ThanyMarcus.Portal");

builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opts.KnownIPNetworks.Clear();
    opts.KnownProxies.Clear();
});

builder.Services.AddScoped<TotpService>();
builder.Services.AddScoped<TotpBackupCodeService>();

builder.Services.AddScoped<PassphraseService>();
builder.Services.AddScoped<EmergencyKitService>();
builder.Services.AddSingleton(new PassphraseValidator(CommonPasswords.Load()));
builder.Services.AddScoped<IInfraOpUnlockCache, PostgresInfraOpUnlockCache>();
builder.Services.AddHostedService<InfraOpUnlockSweepService>();

builder.Services.AddScoped<ISagaCredentialGrantStore, PostgresSagaCredentialGrantStore>();
builder.Services.AddScoped<ISagaCredentialSource, SagaCredentialSource>();

builder.Services.AddScoped<IProviderTokenVault, ProviderTokenVault>();
builder.Services.AddScoped<ICloudSecretBundle, CloudSecretBundle>();

builder.Services.Configure<DigitalOceanOAuthOptions>(builder.Configuration.GetSection("DigitalOcean:OAuth"));
builder.Services.AddSingleton<DigitalOceanOAuthStateCookie>();
builder.Services.AddScoped<IDigitalOceanOAuthConnections, DigitalOceanOAuthConnections>();
builder.Services.AddScoped<DigitalOceanTokenRefresher>();
builder.Services.AddHttpClient<IDigitalOceanOAuthClient, DigitalOceanOAuthClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
})
.AddPolicyHandler(Polly.Extensions.Http.HttpPolicyExtensions
    .HandleTransientHttpError()
    .WaitAndRetryAsync(3, n => TimeSpan.FromMilliseconds(200 * Math.Pow(5, n - 1))));
builder.Services.AddScoped<ICloudAdminTokenAccessor, CloudAdminTokenAccessor>();

builder.Services.AddScoped<IProvisioningProvider, StubProvisioningProvider>();
builder.Services.AddScoped<IProvisioningProvider, DigitalOceanProvisioningProvider>();
builder.Services.AddScoped<IProvisioningProviderRegistry, ProvisioningProviderRegistry>();

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient(DoSizesCatalog.HttpClientName, c =>
{
    c.BaseAddress = new Uri("https://api.digitalocean.com/");
    c.Timeout = TimeSpan.FromSeconds(15);
})
.AddPolicyHandler(Polly.Extensions.Http.HttpPolicyExtensions
    .HandleTransientHttpError()
    .WaitAndRetryAsync(2, n => TimeSpan.FromMilliseconds(200 * Math.Pow(3, n - 1))));
builder.Services.AddSingleton<IDoSizesCatalog, DoSizesCatalog>();

builder.Services.Configure<PluginTokenSyncOptions>(
    builder.Configuration.GetSection(PluginTokenSyncOptions.SectionName));
builder.Services.AddSingleton<IPortalToCloudPluginTokenClient, PortalToCloudPluginTokenClient>();
var pluginTokenSyncOpts = builder.Configuration
    .GetSection(PluginTokenSyncOptions.SectionName)
    .Get<PluginTokenSyncOptions>() ?? new PluginTokenSyncOptions();
builder.Services.AddHttpClient(PortalToCloudPluginTokenClient.HttpClientName, c =>
    c.Timeout = TimeSpan.FromSeconds(pluginTokenSyncOpts.HttpTimeoutSeconds));

builder.Services.Configure<ReleaseFeedOptions>(
    builder.Configuration.GetSection(ReleaseFeedOptions.SectionName));

builder.Services.AddScoped<EnqueueGuard>();
builder.Services.AddSingleton<EnrollmentTokenGenerator>();
builder.Services.AddSingleton<IRandomHexProvider, CryptoRandomHexProvider>();
builder.Services.AddScoped<HostnameGenerator>();
builder.Services.AddScoped<SagaEventTranslator>();
builder.Services.AddScoped<IProvisioningEventBus, PostgresProvisioningEventBus>();
builder.Services.AddSingleton<NpgsqlConnectionFactory>();

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
builder.Services.AddTurnstile(builder.Configuration);

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
    await db.Database.MigrateAsync();
}

app.UseStaticFiles();

app.UseForwardedHeaders();

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
app.MapPasskeyEndpoints();
app.MapPasskeySignupEndpoints();
app.MapDigitalOceanOAuthEndpoints();
app.MapDigitalOceanConnectionEndpoints();
app.MapTotpEndpoints();
app.MapPassphraseEndpoints();
app.MapPassphraseResetEndpoints();
app.MapEmergencyKitEndpoints();
app.MapProviderTokenEndpoints();
app.MapCreateCloudEndpoints();
app.MapGetCloudStatusEndpoints();
app.MapCloudEventsEndpoints();
app.MapProviderMetaEndpoints();
app.MapCloudCallbackEndpoints();
app.MapDestroyCloudEndpoints();
app.MapCancelCloudEndpoints();
app.MapMigrateCloudEndpoints();
app.MapPluginTokenEndpoints();
app.MapRetryPluginTokenEndpoint();
app.MapCaptchaEndpoints();
app.MapPricingBackfillEndpoint();
app.MapReleaseRegistrationEndpoint();
app.MapReleaseFeedEndpoints();

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
