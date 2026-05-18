using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public sealed class ExtractionTaskConfiguration : IEntityTypeConfiguration<ExtractionTask>
{
    public void Configure(EntityTypeBuilder<ExtractionTask> builder)
    {
        builder.ToTable("extraction_tasks", t =>
        {
            t.HasCheckConstraint("ck_extraction_tasks_target_sidecar",
                "target_sidecar IN ('ollama','docling','parakeet','url','video')");
            t.HasCheckConstraint("ck_extraction_tasks_status",
                "status IN ('queued','processing','succeeded','failed','skipped')");
        });
        builder.HasKey(e => e.Id);

        builder.Property(e => e.IngestJobId).IsRequired();
        builder.Property(e => e.AttachmentId).IsRequired();
        builder.Property(e => e.TargetSidecar).IsRequired();
        builder.Property(e => e.Status).IsRequired();
        builder.Property(e => e.Attempts).IsRequired().HasDefaultValue((short)0);
        builder.Property(e => e.LastError);
        builder.Property(e => e.LeaseOwner);
        builder.Property(e => e.LeaseExpiresAt);
        builder.Property(e => e.ScheduledAt).IsRequired();
        builder.Property(e => e.StartedAt);
        builder.Property(e => e.FinishedAt);
        builder.Property(e => e.EventsLog).HasColumnType("jsonb").IsRequired()
            .HasDefaultValueSql("'[]'::jsonb");
        builder.Property(e => e.TransitionVersion).IsRequired().HasDefaultValue(0L);
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        builder.HasIndex(e => new { e.TargetSidecar, e.Status, e.ScheduledAt })
            .HasDatabaseName("ix_extraction_tasks_claim")
            .HasFilter("status IN ('queued','processing')");

        builder.HasIndex(e => e.IngestJobId).HasDatabaseName("ix_extraction_tasks_ingest_job_id");

        builder.HasOne<IngestJob>().WithMany().HasForeignKey(e => e.IngestJobId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Attachment>().WithMany().HasForeignKey(e => e.AttachmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
