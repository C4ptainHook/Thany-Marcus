using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("attachments", t =>
        {
            t.HasCheckConstraint("ck_attachments_kind",
                "kind IN ('url','image','voice','file')");
            t.HasCheckConstraint("ck_attachments_mode",
                "mode IN ('extract','reference','metadata')");
            t.HasCheckConstraint("ck_attachments_metadata_only_url",
                "mode <> 'metadata' OR kind = 'url'");
            t.HasCheckConstraint("ck_attachments_status",
                "status IN ('pending','awaiting_upload','uploaded')");
            t.HasCheckConstraint("ck_attachments_extraction_status",
                "extraction_status IN ('pending','extracted','extracted_minimal','skipped','referenced','failed')");
        });
        builder.HasKey(a => a.Id);

        builder.Property(a => a.NoteId).IsRequired();
        builder.Property(a => a.ClientAttachmentId).IsRequired();
        builder.Property(a => a.Kind).IsRequired();
        builder.Property(a => a.Mode).IsRequired().HasDefaultValue("extract");
        builder.Property(a => a.StorageProvider).IsRequired();
        builder.Property(a => a.StorageBucket).IsRequired();
        builder.Property(a => a.StorageKey).IsRequired();

        builder.Property(a => a.ByteSize);
        builder.Property(a => a.MimeType);
        builder.Property(a => a.Sha256);
        builder.Property(a => a.Filename);

        builder.Property(a => a.Status).IsRequired();
        builder.Property(a => a.ExtractionStatus).IsRequired().HasDefaultValue("pending");
        builder.Property(a => a.ExtractedText);
        builder.Property(a => a.ExtractionError);
        builder.Property(a => a.Extra).HasColumnType("jsonb").IsRequired()
            .HasDefaultValueSql("'{}'::jsonb");

        builder.Property(a => a.ParentAttachmentId);
        builder.Property(a => a.ExtractionCacheKey);
        builder.Property(a => a.Url);

        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.UpdatedAt).IsRequired();

        builder.HasIndex(a => a.NoteId);
        builder.HasIndex(a => new { a.NoteId, a.ClientAttachmentId }).IsUnique();
        builder.HasIndex(a => a.StorageKey).IsUnique();

        builder.HasIndex(a => a.ParentAttachmentId)
            .HasDatabaseName("ix_attachments_parent");

        builder.HasIndex(a => new { a.Sha256, a.ExtractionCacheKey })
            .HasDatabaseName("ix_attachments_cache")
            .HasFilter("extracted_text IS NOT NULL");

        builder.HasOne<Note>().WithMany().HasForeignKey(a => a.NoteId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Attachment>().WithMany().HasForeignKey(a => a.ParentAttachmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
