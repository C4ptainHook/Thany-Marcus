using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ThanyMarcus.Cloud.Api.Features.Entities;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public sealed class NoteConfiguration : IEntityTypeConfiguration<Note>
{
    public void Configure(EntityTypeBuilder<Note> builder)
    {
        builder.ToTable("notes", t =>
        {
            t.HasCheckConstraint("ck_notes_status",
                "status IN ('pending','processing','ready','failed')");
            t.HasCheckConstraint("ck_notes_kind",
                "kind IN ('synth_note','entity_stub')");
            t.HasCheckConstraint("ck_notes_llm_mode",
                "llm_mode IS NULL OR llm_mode IN ('safe','unsafe_anthropic','unsafe_openai')");
        });
        builder.HasKey(n => n.Id);

        builder.Property(n => n.ClientNoteId);
        builder.Property(n => n.CapturedAt).IsRequired();
        builder.Property(n => n.Status).IsRequired();
        builder.Property(n => n.Kind).IsRequired().HasDefaultValue(NoteKind.SynthNote);
        builder.Property(n => n.BodyInput).IsRequired();
        builder.Property(n => n.RelativePath);
        builder.Property(n => n.BodyOutput);
        builder.Property(n => n.Tags).HasColumnType("text[]");
        builder.Property(n => n.LlmMode);
        builder.Property(n => n.Provenance).HasColumnType("jsonb");

        builder.Property(n => n.Embedding).HasColumnType("vector(256)");
        builder.Property(n => n.BodyHash).HasColumnType("text");
        builder.Property(n => n.DeletedAt);
        builder.Property(n => n.IsHub).IsRequired().HasDefaultValue(false);
        builder.Property(n => n.HubEntityId);
        builder.Property(n => n.TransitionVersion).IsRequired().HasDefaultValue(0L);

        builder.Property(n => n.PrivacyMode).HasColumnType("text");
        builder.Property(n => n.PublicModel).HasColumnType("text");
        builder.Property(n => n.SynthesisPreset).HasColumnType("text");
        builder.Property(n => n.SynthesisPromptBody).HasColumnType("text");
        builder.Property(n => n.SynthesisCacheKey).HasColumnType("text");
        builder.Property(n => n.SynthesisCacheValue).HasColumnType("text");

        builder.HasIndex(n => n.SynthesisCacheKey)
            .HasDatabaseName("ix_notes_synthesis_cache_key")
            .HasFilter("synthesis_cache_key IS NOT NULL");

        builder.Property(n => n.CreatedAt).IsRequired();
        builder.Property(n => n.UpdatedAt).IsRequired();

        builder.HasIndex(n => n.ClientNoteId)
            .IsUnique()
            .HasDatabaseName("ix_notes_client_note_id")
            .HasFilter("client_note_id IS NOT NULL");

        builder.HasIndex(n => new { n.Status, n.UpdatedAt })
            .HasDatabaseName("ix_notes_status_updated_at");

        builder.HasIndex(n => n.Kind)
            .HasDatabaseName("ix_notes_kind")
            .HasFilter("deleted_at IS NULL");

        builder.HasIndex(n => n.UpdatedAt)
            .HasDatabaseName("ix_notes_updated_at")
            .HasFilter("deleted_at IS NULL OR status = 'ready'");

        builder.HasIndex(n => n.HubEntityId)
            .HasDatabaseName("ix_notes_hub_entity")
            .HasFilter("is_hub");

        builder.HasIndex(n => n.Embedding)
            .HasDatabaseName("ix_notes_embedding")
            .HasMethod("hnsw")
            .HasOperators("vector_cosine_ops")
            .HasFilter("deleted_at IS NULL");

        builder.HasOne<Entity>().WithMany().HasForeignKey(n => n.HubEntityId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
