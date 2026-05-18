using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public sealed class NoteConfiguration : IEntityTypeConfiguration<Note>
{
    public void Configure(EntityTypeBuilder<Note> builder)
    {
        builder.ToTable("notes", t =>
        {
            t.HasCheckConstraint("ck_notes_status",
                "status IN ('pending','processing','ready','failed')");
            t.HasCheckConstraint("ck_notes_llm_mode",
                "llm_mode IS NULL OR llm_mode IN ('safe','unsafe_anthropic','unsafe_openai')");
        });
        builder.HasKey(n => n.Id);

        builder.Property(n => n.ClientNoteId);
        builder.Property(n => n.CapturedAt).IsRequired();
        builder.Property(n => n.Status).IsRequired();
        builder.Property(n => n.BodyInput).IsRequired();
        builder.Property(n => n.RelativePath);
        builder.Property(n => n.BodyOutput);
        builder.Property(n => n.SuggestedProject);
        builder.Property(n => n.Tags).HasColumnType("text[]");
        builder.Property(n => n.LlmMode);
        builder.Property(n => n.Provenance).HasColumnType("jsonb");

        builder.Property(n => n.CreatedAt).IsRequired();
        builder.Property(n => n.UpdatedAt).IsRequired();

        builder.HasIndex(n => n.ClientNoteId).IsUnique();
        builder.HasIndex(n => new { n.Status, n.UpdatedAt });
    }
}
