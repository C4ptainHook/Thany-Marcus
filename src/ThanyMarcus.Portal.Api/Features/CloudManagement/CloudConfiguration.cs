using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ThanyMarcus.Portal.Api.Features.Auth;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement;

public sealed class CloudConfiguration : IEntityTypeConfiguration<Cloud>
{
    public void Configure(EntityTypeBuilder<Cloud> builder)
    {
        builder.ToTable("clouds");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).IsRequired();
        builder.Property(c => c.Provider).IsRequired();
        builder.Property(c => c.Region).IsRequired();
        builder.Property(c => c.Hostname).IsRequired();
        builder.Property(c => c.ProvisioningStatus).IsRequired();

        builder.Property(c => c.PriceMonthlyUsd).HasColumnType("numeric(10,4)");
        builder.Property(c => c.PriceHourlyUsd).HasColumnType("numeric(10,6)");
        builder.Property(c => c.PriceCurrency).HasDefaultValue("USD");
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();

        builder.HasIndex(c => c.UserId);
        builder.HasIndex(c => c.Hostname).IsUnique();
        builder.HasIndex(c => c.Subdomain).IsUnique();

        builder.HasOne<User>().WithMany().HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<EncryptedProviderToken>().WithMany().HasForeignKey(c => c.ProviderTokenId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(c => c.DestroyedAt == null);
    }
}
