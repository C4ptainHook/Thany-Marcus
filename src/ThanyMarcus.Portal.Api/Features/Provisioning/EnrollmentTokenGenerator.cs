using System.Security.Cryptography;

namespace ThanyMarcus.Portal.Api.Features.Provisioning;

public sealed class EnrollmentTokenGenerator
{
    public string Generate() => RandomNumberGenerator.GetHexString(64).ToLowerInvariant();
}
