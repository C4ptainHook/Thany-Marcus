using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Features.Settings;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database;

public sealed class CloudDbContext(DbContextOptions<CloudDbContext> options) : DbContext(options)
{
    public DbSet<PluginToken> PluginTokens => Set<PluginToken>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<IngestJob> IngestJobs => Set<IngestJob>();
    public DbSet<CloudSettings> CloudSettings => Set<CloudSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudDbContext).Assembly);
    }
}
