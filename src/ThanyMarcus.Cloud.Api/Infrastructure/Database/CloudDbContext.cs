using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.EntitySuggestions;
using ThanyMarcus.Cloud.Api.Features.Folders;
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
    public DbSet<Entity> Entities => Set<Entity>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<Mention> Mentions => Set<Mention>();
    public DbSet<EntitySuggestion> EntitySuggestions => Set<EntitySuggestion>();
    public DbSet<ExtractionTask> ExtractionTasks => Set<ExtractionTask>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudDbContext).Assembly);
    }
}
