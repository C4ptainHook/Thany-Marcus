using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement;

public sealed class PluginTokenMetadataConfiguration : IEntityTypeConfiguration<PluginTokenMetadata>
{
    public void Configure(EntityTypeBuilder<PluginTokenMetadata> builder)
    {
        builder.ToTable("plugin_token_metadata");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name).IsRequired();
        builder.Property(p => p.TokenHash).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        builder.HasIndex(p => p.CloudId);
        builder.HasIndex(p => p.TokenHash).IsUnique();

        builder.HasOne<Cloud>().WithMany().HasForeignKey(p => p.CloudId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
