using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Entities;

public sealed class MentionConfiguration : IEntityTypeConfiguration<Mention>
{
    public void Configure(EntityTypeBuilder<Mention> builder)
    {
        builder.ToTable("mentions");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.EntityId).IsRequired();
        builder.Property(m => m.NoteId).IsRequired();
        builder.Property(m => m.AnchorText).IsRequired();
        builder.Property(m => m.StartOffset).IsRequired();
        builder.Property(m => m.EndOffset).IsRequired();
        builder.Property(m => m.Confidence).HasColumnType("real");
        builder.Property(m => m.CreatedAt).IsRequired();

        builder.HasIndex(m => m.EntityId).HasDatabaseName("ix_mentions_entity_id");
        builder.HasIndex(m => m.NoteId).HasDatabaseName("ix_mentions_note_id");

        builder.HasOne<Entity>().WithMany().HasForeignKey(m => m.EntityId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Note>().WithMany().HasForeignKey(m => m.NoteId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
