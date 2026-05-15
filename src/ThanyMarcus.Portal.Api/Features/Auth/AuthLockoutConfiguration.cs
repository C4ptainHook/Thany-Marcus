using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class AuthLockoutConfiguration : IEntityTypeConfiguration<AuthLockout>
{
    public void Configure(EntityTypeBuilder<AuthLockout> builder)
    {
        builder.ToTable("auth_lockouts");
        builder.HasKey(a => new { a.UserId, a.Kind });

        builder.Property(a => a.Kind).IsRequired();
        builder.Property(a => a.FailedCount).IsRequired();
        builder.Property(a => a.LastAttemptAt).IsRequired();

        builder.HasOne<User>().WithMany().HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
