using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class RecoveryCodeConfiguration : IEntityTypeConfiguration<RecoveryCode>
{
    public void Configure(EntityTypeBuilder<RecoveryCode> builder)
    {
        builder.ToTable("recovery_codes");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.HashedCode).IsRequired();
        builder.Property(r => r.WrapArgon2Salt).IsRequired();
        builder.Property(r => r.WrapArgon2Params).HasColumnType("jsonb").IsRequired();
        builder.Property(r => r.WrappedDek).IsRequired();
        builder.Property(r => r.WrapNonce).IsRequired();
        builder.Property(r => r.WrapTag).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();

        builder.HasIndex(r => r.UserId);

        builder.HasOne<User>().WithMany().HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
