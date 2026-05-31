using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Portal.Api.Features.Auth.Passkey;

public sealed class PasskeyCredentialConfiguration : IEntityTypeConfiguration<PasskeyCredential>
{
    public void Configure(EntityTypeBuilder<PasskeyCredential> builder)
    {
        builder.ToTable("passkey_credentials");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.UserId).IsRequired();
        builder.Property(p => p.CredentialId).IsRequired();
        builder.Property(p => p.PublicKey).IsRequired();
        builder.Property(p => p.SignCount).IsRequired();
        builder.Property(p => p.Aaguid).IsRequired();
        builder.Property(p => p.AuthenticatorName);
        builder.Property(p => p.Transports).IsRequired();
        builder.Property(p => p.BackedUp).IsRequired();

        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();
        builder.Property(p => p.LastUsedAt);
        builder.Property(p => p.RevokedAt);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => p.CredentialId)
            .IsUnique()
            .HasFilter("revoked_at IS NULL");

        builder.HasIndex(p => p.UserId)
            .HasFilter("revoked_at IS NULL");
    }
}
