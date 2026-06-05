using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Cloud.Api.Features.Entities;

public sealed class EntityConfiguration : IEntityTypeConfiguration<Entity>
{
    public void Configure(EntityTypeBuilder<Entity> builder)
    {
        builder.ToTable("entities", t =>
        {
            t.HasCheckConstraint("ck_entities_kind",
                "kind IN ('person','organization','place','concept','other')");
            t.HasCheckConstraint("ck_entities_source",
                "source IN ('user','llm')");
        });
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Kind).IsRequired();
        builder.Property(e => e.CanonicalName).IsRequired();
        builder.Property(e => e.DisplayName);
        builder.Property(e => e.Aliases)
            .HasColumnType("text[]")
            .IsRequired()
            .HasDefaultValueSql("'{}'::text[]");
        builder.Property(e => e.Description);
        builder.Property(e => e.Embedding).HasColumnType("vector(256)");
        builder.Property(e => e.HubNoteId);
        builder.Property(e => e.HubSuppressed).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.StubNoteId);
        builder.Property(e => e.MentionCount).IsRequired().HasDefaultValue(0);
        builder.Property(e => e.Source).IsRequired();
        builder.Property(e => e.VaultFolder);
        builder.Property(e => e.DeletedAt);
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        builder.HasIndex(e => new { e.Kind, e.CanonicalName })
            .IsUnique()
            .HasDatabaseName("ix_entities_kind_canonical_name")
            .HasFilter("deleted_at IS NULL");

        builder.HasIndex(e => new { e.Kind, e.Source })
            .HasDatabaseName("ix_entities_kind_source");

        builder.HasIndex(e => e.Embedding)
            .HasDatabaseName("ix_entities_embedding")
            .HasMethod("hnsw")
            .HasOperators("vector_cosine_ops");

        builder.HasIndex(e => e.StubNoteId)
            .IsUnique()
            .HasDatabaseName("ix_entities_stub_note")
            .HasFilter("stub_note_id IS NOT NULL");

        builder.HasOne<Ingest.Note>().WithMany().HasForeignKey(e => e.StubNoteId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
