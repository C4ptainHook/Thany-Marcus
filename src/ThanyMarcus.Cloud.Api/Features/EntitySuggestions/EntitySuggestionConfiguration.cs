using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ThanyMarcus.Cloud.Api.Features.Entities;

namespace ThanyMarcus.Cloud.Api.Features.EntitySuggestions;

public sealed class EntitySuggestionConfiguration : IEntityTypeConfiguration<EntitySuggestion>
{
    public void Configure(EntityTypeBuilder<EntitySuggestion> builder)
    {
        builder.ToTable("entity_suggestions", t =>
        {
            t.HasCheckConstraint("ck_entity_suggestions_kind",
                "kind IN ('person','organization','place','concept','other')");
        });
        builder.HasKey(e => e.Id);

        builder.Property(e => e.CanonicalText).IsRequired();
        builder.Property(e => e.Kind).IsRequired();
        builder.Property(e => e.Aliases)
            .HasColumnType("text[]")
            .IsRequired()
            .HasDefaultValueSql("'{}'::text[]");
        builder.Property(e => e.Occurrences)
            .HasColumnType("jsonb")
            .IsRequired()
            .HasDefaultValueSql("'[]'::jsonb");
        builder.Property(e => e.OccurrenceCount).IsRequired().HasDefaultValue(0);
        builder.Property(e => e.DistinctNoteCount).IsRequired().HasDefaultValue(0);
        builder.Property(e => e.Embedding).HasColumnType("vector(256)");
        builder.Property(e => e.FirstSeenAt).IsRequired();
        builder.Property(e => e.LastSeenAt).IsRequired();
        builder.Property(e => e.AcceptedAt);
        builder.Property(e => e.AcceptedEntityId);
        builder.Property(e => e.SuggestedMergeEntityId);
        builder.Property(e => e.SuggestedMergeDistance);
        builder.Property(e => e.DismissedAt);
        builder.Property(e => e.CreatedAt).IsRequired();

        builder.HasIndex(e => new { e.OccurrenceCount, e.LastSeenAt })
            .HasDatabaseName("ix_entity_suggestions_surfaceable")
            .IsDescending(true, true)
            .HasFilter("accepted_at IS NULL AND dismissed_at IS NULL");

        // The unique partial index on (LOWER(canonical_text), kind) is a functional index
        // that the fluent API can't express; it is created by raw SQL in the migration.

        builder.HasOne<Entity>().WithMany().HasForeignKey(e => e.AcceptedEntityId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Entity>().WithMany().HasForeignKey(e => e.SuggestedMergeEntityId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
