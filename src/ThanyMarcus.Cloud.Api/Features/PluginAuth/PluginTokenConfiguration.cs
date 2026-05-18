using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Cloud.Api.Features.PluginAuth;

public sealed class PluginTokenConfiguration : IEntityTypeConfiguration<PluginToken>
{
    public void Configure(EntityTypeBuilder<PluginToken> builder)
    {
        builder.ToTable("plugin_tokens");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash).IsRequired();
        builder.Property(t => t.Label).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.RevokedAt);

        builder.HasIndex(t => t.TokenHash).IsUnique();
    }
}
