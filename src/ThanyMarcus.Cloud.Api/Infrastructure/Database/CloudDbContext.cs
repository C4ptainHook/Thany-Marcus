using Microsoft.EntityFrameworkCore;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database;

public sealed class CloudDbContext(DbContextOptions<CloudDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}
