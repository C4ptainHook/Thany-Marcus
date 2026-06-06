using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.Auth.Passkey;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;
using ThanyMarcus.Portal.Api.Features.Releases;

namespace ThanyMarcus.Portal.Api.Infrastructure.Database;

public sealed class PortalDbContext(DbContextOptions<PortalDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<TotpSecret> TotpSecrets => Set<TotpSecret>();
    public DbSet<PasskeyCredential> PasskeyCredentials => Set<PasskeyCredential>();
    public DbSet<TotpBackupCode> TotpBackupCodes => Set<TotpBackupCode>();
    public DbSet<EmergencyKit> EmergencyKits => Set<EmergencyKit>();
    public DbSet<EncryptedProviderToken> EncryptedProviderTokens => Set<EncryptedProviderToken>();
    public DbSet<DigitalOceanOAuthConnection> DigitalOceanOAuthConnections => Set<DigitalOceanOAuthConnection>();
    public DbSet<AuthLockout> AuthLockouts => Set<AuthLockout>();
    public DbSet<StepUpUnlock> StepUpUnlocks => Set<StepUpUnlock>();
    public DbSet<Cloud> Clouds => Set<Cloud>();
    public DbSet<CloudSecret> CloudSecrets => Set<CloudSecret>();
    public DbSet<PluginTokenMetadata> PluginTokenMetadata => Set<PluginTokenMetadata>();
    public DbSet<ProvisioningJob> ProvisioningJobs => Set<ProvisioningJob>();
    public DbSet<SagaCredentialGrant> SagaCredentialGrants => Set<SagaCredentialGrant>();
    public DbSet<Release> Releases => Set<Release>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PortalDbContext).Assembly);
    }
}
