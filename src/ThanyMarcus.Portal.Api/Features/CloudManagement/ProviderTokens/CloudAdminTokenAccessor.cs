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
        var bytes = await db.ProvisioningJobs
            .Where(j => j.CloudId == cloudId && j.AdminTokenCiphertext != null)
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => j.AdminTokenCiphertext)
            .FirstOrDefaultAsync(ct);
        if (bytes is null) return null;
        var protector = dpp.CreateProtector(DataProtectionPurpose);
        return Encoding.UTF8.GetString(protector.Unprotect(bytes));
    }
}
