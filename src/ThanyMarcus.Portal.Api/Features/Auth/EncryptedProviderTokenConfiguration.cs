using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class EncryptedProviderTokenConfiguration : IEntityTypeConfiguration<EncryptedProviderToken>
{
    public void Configure(EntityTypeBuilder<EncryptedProviderToken> builder)
    {
        builder.ToTable("encrypted_provider_tokens");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Provider).IsRequired();
        builder.Property(e => e.Ciphertext).IsRequired();
        builder.Property(e => e.Nonce).IsRequired();
        builder.Property(e => e.Tag).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        builder.HasIndex(e => new { e.UserId, e.Provider }).IsUnique();

        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
