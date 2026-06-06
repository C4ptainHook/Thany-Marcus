using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Portal.Api.Features.Releases;

public sealed class ReleaseConfiguration : IEntityTypeConfiguration<Release>
{
    public void Configure(EntityTypeBuilder<Release> builder)
    {
        builder.ToTable("releases");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Version).IsRequired();
        builder.Property(r => r.Strategy).IsRequired();
        builder.Property(r => r.ComposeYaml).IsRequired();
        builder.Property(r => r.ImageDigests).HasColumnType("jsonb").IsRequired();
        builder.Property(r => r.ModelTags).HasColumnType("jsonb").IsRequired();
        builder.Property(r => r.EnvOverlay).HasColumnType("jsonb").IsRequired();
        builder.Property(r => r.SchemaMinFrom).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        builder.HasIndex(r => r.Version).IsUnique();
    }
}
