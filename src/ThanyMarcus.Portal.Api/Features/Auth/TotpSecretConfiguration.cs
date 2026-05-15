using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class TotpSecretConfiguration : IEntityTypeConfiguration<TotpSecret>
{
    public void Configure(EntityTypeBuilder<TotpSecret> builder)
    {
        builder.ToTable("totp_secrets");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Ciphertext).IsRequired();
        builder.Property(t => t.Nonce).IsRequired();
        builder.Property(t => t.Tag).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();

        builder.HasIndex(t => t.UserId).IsUnique();

        builder.HasOne<User>().WithOne().HasForeignKey<TotpSecret>(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
