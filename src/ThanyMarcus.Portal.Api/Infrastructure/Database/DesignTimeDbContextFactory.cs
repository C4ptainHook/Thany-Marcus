using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ThanyMarcus.Portal.Api.Infrastructure.Database;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PortalDbContext>
{
    public PortalDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PORTAL_DESIGN_TIME_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=portal_design";

        var options = new DbContextOptionsBuilder<PortalDbContext>()
            .UseNpgsql(connectionString, npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new PortalDbContext(options);
    }
}
