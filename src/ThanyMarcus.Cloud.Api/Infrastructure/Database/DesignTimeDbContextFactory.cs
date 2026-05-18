using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CloudDbContext>
{
    public CloudDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CLOUD_DESIGN_TIME_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=cloud_design";

        var options = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(connectionString, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new CloudDbContext(options);
    }
}
