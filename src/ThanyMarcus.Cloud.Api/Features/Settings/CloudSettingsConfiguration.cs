using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;

namespace ThanyMarcus.Cloud.Api.Features.Settings;

public sealed class CloudSettingsConfiguration : IEntityTypeConfiguration<CloudSettings>
{
    private static readonly Instant SeedInstant = Instant.FromUtc(2026, 5, 18, 0, 0);

    public void Configure(EntityTypeBuilder<CloudSettings> builder)
    {
        builder.ToTable("cloud_settings", t =>
        {
            t.HasCheckConstraint("ck_cloud_settings_singleton", "id = 1");
            t.HasCheckConstraint("ck_cloud_settings_llm_mode",
                "llm_mode IN ('safe','unsafe_anthropic','unsafe_openai')");
        });
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever().HasDefaultValue(CloudSettings.SingletonId);

        builder.Property(s => s.LlmMode).IsRequired().HasDefaultValue("safe");
        builder.Property(s => s.EncryptedExternalApiKey);
        builder.Property(s => s.LlmModel);
        builder.Property(s => s.RelatedNotesMaxDistanceAuto);
        builder.Property(s => s.RelatedNotesAutoNoteCount);
        builder.Property(s => s.RelatedNotesAutoEntityCount);
        builder.Property(s => s.BootstrapConsumedAt);
        builder.Property(s => s.RecoveryAnchorHash);
        builder.Property(s => s.ReindexInProgress).IsRequired().HasDefaultValue(false);
        builder.Property(s => s.UpdatedAt).IsRequired();

        builder.HasData(new CloudSettings
        {
            Id        = CloudSettings.SingletonId,
            LlmMode   = LlmModes.Safe,
            UpdatedAt = SeedInstant,
        });
    }
}
