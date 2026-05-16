using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Portal.Api.Features.Auth.StepUp;

public sealed class StepUpUnlockConfiguration : IEntityTypeConfiguration<StepUpUnlock>
{
    public void Configure(EntityTypeBuilder<StepUpUnlock> builder)
    {
        builder.ToTable("step_up_unlocks");
        builder.HasKey(u => u.UserId);
        builder.Property(u => u.EncryptedDek).IsRequired();
        builder.Property(u => u.ExpiresAt).IsRequired();
        builder.Property(u => u.LastUsedAt).IsRequired();
        builder.Property(u => u.CreatedAt).IsRequired();
        builder.HasIndex(u => u.ExpiresAt);
        builder.HasOne<User>().WithMany().HasForeignKey(u => u.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
