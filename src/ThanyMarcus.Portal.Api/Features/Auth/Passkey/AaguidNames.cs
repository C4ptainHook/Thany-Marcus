namespace ThanyMarcus.Portal.Api.Features.Auth.Passkey;

/// <summary>
/// Best-effort static map of well-known authenticator AAGUIDs to human-friendly names.
/// Sourced from the community passkey-authenticator-aaguids list. Unknown GUIDs fall back
/// to a generic label; a periodic refresh from FIDO MDS3 is a future improvement.
/// </summary>
public static class AaguidNames
{
    public const string Fallback = "Unknown authenticator";

    private static readonly Dictionary<Guid, string> Names = new()
    {
        [new("fbfc3007-154e-4ecc-8c0b-6e020557d7bd")] = "iCloud Keychain",
        [new("ea9b8d66-4d01-1d21-3ce4-b6b48cb575d4")] = "Google Password Manager",
        [new("08987058-cadc-4b81-b6e1-30de50dcbe96")] = "Windows Hello",
        [new("9ddd1817-af5a-4672-a2b9-3e3dd95000a5")] = "Windows Hello",
        [new("6028b017-b1d4-4c02-b4b3-afcdafc96bb2")] = "Windows Hello",
        [new("bada5566-a7aa-401f-bd96-45619a55120d")] = "1Password",
        [new("d548826e-79b4-db40-a3d8-11116f7e8349")] = "Bitwarden",
        [new("531126d6-e717-415c-9320-3d9aa6981239")] = "Dashlane",
        [new("0ea242b4-43c4-4a1b-8b17-dd6d0b6baec6")] = "Keeper",
        [new("b84e4048-15dc-4dd0-8640-f4f60813c8af")] = "NordPass",
        [new("f8a011f3-8c0a-4d15-8006-17111f9edc7d")] = "Security Key by Yubico",
        [new("cb69481e-8ff7-4039-93ec-0a2729a154a8")] = "YubiKey 5 NFC",
        [new("ee882879-721c-4913-9775-3dfcce97072a")] = "YubiKey 5 Series",
        [new("fa2b99dc-9e39-4257-8f92-4a30d23c4118")] = "YubiKey 5 NFC",
        [new("2fc0579f-8113-47ea-b116-bb5a8db9202a")] = "YubiKey 5 Series",
        [new("73bb0cd4-e502-49b8-9c6f-b59445bf720b")] = "YubiKey 5 FIPS",
        [new("c5ef55ff-ad9a-4b9f-b580-adebafe026d0")] = "YubiKey 5Ci",
        [new("d8522d9f-575b-4866-88a9-ba99fa02f35b")] = "YubiKey Bio",
    };

    public static string Resolve(Guid aaguid)
        => Names.TryGetValue(aaguid, out var name) ? name : Fallback;
}
