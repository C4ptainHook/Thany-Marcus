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
                "status IN (" +
                "'queued','extracting_attachments','composing','extracting_entities'," +
                "'routing','synthesizing','embedding','succeeded'," +
                "'failed_extraction','failed_composition','failed_entities','failed_route'," +
                "'failed_synthesis','failed_embedding','dead_lettered','cancelled')");
            t.HasCheckConstraint("ck_ingest_jobs_kind",
                "kind IN ('capture','hub_regen','reprocess','user_edit_embed')");
        });
        builder.HasKey(j => j.Id);

        builder.Property(j => j.NoteId).IsRequired();
        builder.Property(j => j.Kind).IsRequired().HasDefaultValue(IngestJobKind.Capture);
        builder.Property(j => j.Status).IsRequired();
        builder.Property(j => j.Attempts).IsRequired();
        builder.Property(j => j.ConsecutiveCrashes).IsRequired().HasDefaultValue((short)0);
        builder.Property(j => j.LastError);
        builder.Property(j => j.LeaseOwner);
        builder.Property(j => j.LeaseExpiresAt);
        builder.Property(j => j.ScheduledAt).IsRequired();
        builder.Property(j => j.StartedAt);
        builder.Property(j => j.FinishedAt);
        builder.Property(j => j.EventsLog).HasColumnType("jsonb").IsRequired()
            .HasDefaultValueSql("'[]'::jsonb");
        builder.Property(j => j.TransitionVersion).IsRequired().HasDefaultValue(0L);
        builder.Property(j => j.CreatedAt).IsRequired();
        builder.Property(j => j.UpdatedAt).IsRequired();

        builder.HasIndex(j => new { j.Status, j.ScheduledAt })
            .HasDatabaseName("ix_ingest_jobs_queued_scheduled_at")
            .HasFilter("status = 'queued'");

        builder.HasIndex([nameof(IngestJob.NoteId)], "ix_ingest_jobs_note_id")
            .HasDatabaseName("ix_ingest_jobs_note_id");

        builder.HasIndex([nameof(IngestJob.NoteId)], "ix_ingest_jobs_active_per_note")
            .HasDatabaseName("ix_ingest_jobs_active_per_note")
            .IsUnique()
            .HasFilter("status NOT IN ('succeeded','failed_extraction','failed_composition'," +
                       "'failed_entities','failed_route','failed_synthesis','failed_embedding','dead_lettered','cancelled')");

        builder.HasOne<Note>().WithMany().HasForeignKey(j => j.NoteId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
