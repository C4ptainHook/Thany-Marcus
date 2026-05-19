using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;

public sealed class CloudSecretConfiguration : IEntityTypeConfiguration<CloudSecret>
{
    public void Configure(EntityTypeBuilder<CloudSecret> builder)
    {
        builder.ToTable("cloud_secrets");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Kind).IsRequired();
        builder.Property(s => s.Ciphertext).IsRequired();
        builder.Property(s => s.Nonce).IsRequired();
        builder.Property(s => s.Tag).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        builder.HasIndex(s => new { s.CloudId, s.Kind }).IsUnique();

        builder.HasOne<Cloud>().WithMany().HasForeignKey(s => s.CloudId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
