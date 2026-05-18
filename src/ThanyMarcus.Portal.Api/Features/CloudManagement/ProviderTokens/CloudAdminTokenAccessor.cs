using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;

public sealed class CloudAdminTokenAccessor(
    PortalDbContext db,
    IDataProtectionProvider dpp) : ICloudAdminTokenAccessor
{
    public const string DataProtectionPurpose = "cloud-admin-token:v1";

    public async Task<string?> GetPlaintextAsync(Guid cloudId, CancellationToken ct)
    {
        var bytes = await db.Clouds.IgnoreQueryFilters()
            .Where(c => c.Id == cloudId)
            .Select(c => c.EncryptedCloudAdminToken)
            .SingleOrDefaultAsync(ct);
        if (bytes is null) return null;
        var protector = dpp.CreateProtector(DataProtectionPurpose);
        return Encoding.UTF8.GetString(protector.Unprotect(bytes));
    }
}
