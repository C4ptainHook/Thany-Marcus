using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class TotpBackupCodeConfiguration : IEntityTypeConfiguration<TotpBackupCode>
{
    public void Configure(EntityTypeBuilder<TotpBackupCode> builder)
    {
        builder.ToTable("totp_backup_codes");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.HashedCode).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();

        builder.HasIndex(t => t.UserId);

        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
