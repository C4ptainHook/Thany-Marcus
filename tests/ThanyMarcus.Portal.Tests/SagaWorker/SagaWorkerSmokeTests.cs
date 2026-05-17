using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Shared.Database;
using ThanyMarcus.Portal.Tests.Infrastructure;
using SagaWorkerService = ThanyMarcus.Portal.SagaWorker.SagaWorker;

namespace ThanyMarcus.Portal.Tests.SagaWorker;

[Collection(PostgresCollection.Name)]
public sealed class SagaWorkerSmokeTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Worker_starts_and_stops_cleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpKeysDir = Path.Combine(Path.GetTempPath(), "saga-worker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dpKeysDir);

        try
        {
            var builder = Host.CreateApplicationBuilder();

            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Portal"] = postgres.ConnectionString,
                ["DataProtection:KeyRingPath"] = dpKeysDir,
            });

            builder.Services.AddSingleton<IClock>(SystemClock.Instance);
            builder.Services.AddSingleton<TimestampInterceptor>();
            builder.Services.AddDbContext<PortalDbContext>((sp, opts) => opts
                .UseNpgsql(postgres.ConnectionString, npg => npg.UseNodaTime())
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));
            builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir))
                .SetApplicationName("ThanyMarcus.Portal");
            builder.Services.AddScoped<IInfraOpUnlockCache, PostgresInfraOpUnlockCache>();
            builder.Services.AddHostedService<SagaWorkerService>();

            using var host = builder.Build();
            await host.StartAsync(ct);
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            await host.StopAsync(ct);
        }
        finally
        {
            if (Directory.Exists(dpKeysDir))
            {
                Directory.Delete(dpKeysDir, recursive: true);
            }
        }
    }
}
