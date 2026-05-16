using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;

namespace ThanyMarcus.Portal.Api.Infrastructure.Database;

public sealed class PortalDbContext(DbContextOptions<PortalDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<TotpSecret> TotpSecrets => Set<TotpSecret>();
    public DbSet<TotpBackupCode> TotpBackupCodes => Set<TotpBackupCode>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<EncryptedProviderToken> EncryptedProviderTokens => Set<EncryptedProviderToken>();
    public DbSet<AuthLockout> AuthLockouts => Set<AuthLockout>();
    public DbSet<StepUpUnlock> StepUpUnlocks => Set<StepUpUnlock>();
    public DbSet<Cloud> Clouds => Set<Cloud>();
    public DbSet<PluginTokenMetadata> PluginTokenMetadata => Set<PluginTokenMetadata>();
    public DbSet<ProvisioningJob> ProvisioningJobs => Set<ProvisioningJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PortalDbContext).Assembly);
    }
}
