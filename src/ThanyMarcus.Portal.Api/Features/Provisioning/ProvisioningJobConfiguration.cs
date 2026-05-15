using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ThanyMarcus.Portal.Api.Features.CloudManagement;

namespace ThanyMarcus.Portal.Api.Features.Provisioning;

public sealed class ProvisioningJobConfiguration : IEntityTypeConfiguration<ProvisioningJob>
{
    public void Configure(EntityTypeBuilder<ProvisioningJob> builder)
    {
        builder.ToTable("provisioning_jobs");
        builder.HasKey(j => j.Id);

        builder.Property(j => j.Kind).IsRequired();
        builder.Property(j => j.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(j => j.Status).IsRequired();
        builder.Property(j => j.Attempts).IsRequired();
        builder.Property(j => j.CreatedAt).IsRequired();
        builder.Property(j => j.UpdatedAt).IsRequired();

        builder.HasIndex(j => j.CreatedAt)
            .HasDatabaseName("ix_provisioning_jobs_pending_created_at")
            .HasFilter("status = 'pending'");

        builder.HasIndex(j => j.LeaseExpires)
            .HasDatabaseName("ix_provisioning_jobs_inprogress_lease")
            .HasFilter("status = 'in_progress'");

        builder.HasIndex(j => j.CloudId);

        builder.HasOne<Cloud>().WithMany().HasForeignKey(j => j.CloudId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
