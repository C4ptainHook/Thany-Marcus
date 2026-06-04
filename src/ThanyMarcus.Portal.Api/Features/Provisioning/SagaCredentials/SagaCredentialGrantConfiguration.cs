using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ThanyMarcus.Portal.Api.Features.CloudManagement;

namespace ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;

public sealed class SagaCredentialGrantConfiguration : IEntityTypeConfiguration<SagaCredentialGrant>
{
    public void Configure(EntityTypeBuilder<SagaCredentialGrant> builder)
    {
        builder.ToTable("saga_credential_grants");
        builder.HasKey(g => g.CloudId);

        builder.Property(g => g.SealedDek).IsRequired();
        builder.Property(g => g.CreatedAt).IsRequired();
        builder.Property(g => g.ExpiresAt).IsRequired();

        builder.HasIndex(g => g.ExpiresAt);

        builder.HasOne<Cloud>().WithMany().HasForeignKey(g => g.CloudId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
