using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;

public sealed class DigitalOceanOAuthConnectionConfiguration : IEntityTypeConfiguration<DigitalOceanOAuthConnection>
{
    public void Configure(EntityTypeBuilder<DigitalOceanOAuthConnection> builder)
    {
        builder.ToTable("digitalocean_oauth_connections");
        builder.HasKey(c => c.UserId);

        builder.Property(c => c.AccessCiphertext).IsRequired();
        builder.Property(c => c.AccessNonce).IsRequired();
        builder.Property(c => c.AccessTag).IsRequired();
        builder.Property(c => c.AccessExpiresAt).IsRequired();

        builder.Property(c => c.RefreshCiphertext).IsRequired();
        builder.Property(c => c.RefreshNonce).IsRequired();
        builder.Property(c => c.RefreshTag).IsRequired();

        builder.Property(c => c.ConnectionStatus).IsRequired().HasDefaultValue("connected");

        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();

        builder.HasOne<User>().WithMany().HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
