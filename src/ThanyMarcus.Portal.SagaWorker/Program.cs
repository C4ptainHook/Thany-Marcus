using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.SagaWorker;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.UseUtcTimestamp = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: "ThanyMarcus.Portal.SagaWorker",
                serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev"))
            .WithMetrics(m => m
                .AddRuntimeInstrumentation()
                .AddProcessInstrumentation()
                .AddConsoleExporter())
            .WithTracing(t => t
                .AddConsoleExporter());

        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddSingleton<TimestampInterceptor>();

        builder.Services.AddDbContext<PortalDbContext>((sp, opts) => opts
            .UseNpgsql(
                builder.Configuration.GetConnectionString("Portal")
                    ?? throw new InvalidOperationException("ConnectionStrings:Portal not configured"),
                npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));

        var dpKeysDir = builder.Configuration["DataProtection:KeyRingPath"]
            ?? throw new InvalidOperationException("DataProtection:KeyRingPath not configured (required for cross-process key sharing)");
        Directory.CreateDirectory(dpKeysDir);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir))
            .SetApplicationName("ThanyMarcus.Portal");

        builder.Services.AddScoped<IInfraOpUnlockCache, PostgresInfraOpUnlockCache>();

        builder.Services.AddHostedService<SagaWorker>();

        var host = builder.Build();
        await host.RunAsync();
    }
}
