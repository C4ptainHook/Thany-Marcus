using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public sealed class IngestJobConfiguration : IEntityTypeConfiguration<IngestJob>
{
    public void Configure(EntityTypeBuilder<IngestJob> builder)
    {
        builder.ToTable("ingest_jobs", t =>
        {
            t.HasCheckConstraint("ck_ingest_jobs_status",
                "status IN ('queued','processing','succeeded','dead_lettered')");
        });
        builder.HasKey(j => j.Id);

        builder.Property(j => j.NoteId).IsRequired();
        builder.Property(j => j.Status).IsRequired();
        builder.Property(j => j.Attempts).IsRequired();
        builder.Property(j => j.LastError);
        builder.Property(j => j.LeaseOwner);
        builder.Property(j => j.LeaseExpiresAt);
        builder.Property(j => j.ScheduledAt).IsRequired();
        builder.Property(j => j.StartedAt);
        builder.Property(j => j.FinishedAt);
        builder.Property(j => j.CreatedAt).IsRequired();
        builder.Property(j => j.UpdatedAt).IsRequired();

        builder.HasIndex(j => new { j.Status, j.ScheduledAt })
            .HasDatabaseName("ix_ingest_jobs_queued_scheduled_at")
            .HasFilter("status = 'queued'");

        builder.HasIndex(j => j.NoteId);

        builder.HasOne<Note>().WithMany().HasForeignKey(j => j.NoteId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
