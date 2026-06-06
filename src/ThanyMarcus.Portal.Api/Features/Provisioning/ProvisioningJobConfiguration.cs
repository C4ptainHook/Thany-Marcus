using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.CloudManagement;

namespace ThanyMarcus.Portal.Api.Features.Provisioning;

public sealed class ProvisioningJobConfiguration : IEntityTypeConfiguration<ProvisioningJob>
{
    private const string TerminalStatusFilter =
        "status NOT IN ('succeeded','failed_tf','failed_dns','failed_callback','failed_cert','failed_destroy','cancelled','rolled_back')";

    private const string ActiveCreateFilter =
        "kind = 'create' AND status NOT IN ('succeeded','failed_tf','failed_dns','failed_callback','failed_cert','failed_destroy','cancelled','rolled_back')";

    public void Configure(EntityTypeBuilder<ProvisioningJob> builder)
    {
        builder.ToTable("provisioning_jobs");
        builder.HasKey(j => j.Id);

        builder.Property(j => j.Kind).IsRequired();
        builder.Property(j => j.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(j => j.Status).IsRequired();
        builder.Property(j => j.NextVisibleAt).IsRequired();
        builder.Property(j => j.AttemptCount).IsRequired();
        builder.Property(j => j.TransitionVersion)
            .IsRequired()
            .HasDefaultValue(0L)
            .IsConcurrencyToken();
        builder.Property(j => j.EventsLog).HasColumnType("jsonb").IsRequired()
            .HasDefaultValueSql("'[]'::jsonb");
        builder.Property(j => j.TfOutputs).HasColumnType("jsonb");
        builder.Property(j => j.EnrollmentToken);
        builder.Property(j => j.AdminTokenCiphertext);
        builder.Property(j => j.CreatedAt).IsRequired();
        builder.Property(j => j.UpdatedAt).IsRequired();

        builder.HasIndex(j => j.NextVisibleAt)
            .HasDatabaseName("ix_provisioning_jobs_active_next_visible_at")
            .HasFilter(TerminalStatusFilter);

        builder.HasIndex(j => j.CloudId);

        builder.HasIndex(j => j.UserId)
            .HasDatabaseName("ix_provisioning_jobs_user_create_active")
            .HasFilter(ActiveCreateFilter);

        builder.HasOne<Cloud>().WithMany().HasForeignKey(j => j.CloudId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>().WithMany().HasForeignKey(j => j.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
