using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class EmergencyKitConfiguration : IEntityTypeConfiguration<EmergencyKit>
{
    public void Configure(EntityTypeBuilder<EmergencyKit> builder)
    {
        builder.ToTable("emergency_kits");
        builder.HasKey(k => k.Id);

        builder.Property(k => k.HashedString).IsRequired();
        builder.Property(k => k.WrapArgon2Salt).IsRequired();
        builder.Property(k => k.WrapArgon2Params).HasColumnType("jsonb").IsRequired();
        builder.Property(k => k.WrappedDek).IsRequired();
        builder.Property(k => k.WrapNonce).IsRequired();
        builder.Property(k => k.WrapTag).IsRequired();
        builder.Property(k => k.CreatedAt).IsRequired();

        // At most one active (unredeemed, unrevoked) kit per user.
        builder.HasIndex(k => k.UserId)
            .IsUnique()
            .HasDatabaseName("ix_emergency_kits_active_per_user")
            .HasFilter("used_at IS NULL AND revoked_at IS NULL");

        // The one-to-many FK convention also gives us a plain ix_emergency_kits_user_id.
        builder.HasOne<User>().WithMany().HasForeignKey(k => k.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
