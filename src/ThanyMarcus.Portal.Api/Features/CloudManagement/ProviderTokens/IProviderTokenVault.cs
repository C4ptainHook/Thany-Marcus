namespace ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;

public interface IProviderTokenVault
{
    /// <summary>
    /// Encrypts <paramref name="plaintextToken"/> with the user's DEK and persists.
    /// Throws <see cref="ProviderTokenAlreadyExistsException"/> if a token already exists for
    /// <paramref name="provider"/> (call <see cref="ReplaceAsync"/> instead).
    /// </summary>
    Task AddAsync(Guid userId, string provider, string plaintextToken, ReadOnlyMemory<byte> dek, CancellationToken ct);

    /// <summary>Encrypt-and-store, replacing any existing token for the user+provider.</summary>
    Task ReplaceAsync(Guid userId, string provider, string plaintextToken, ReadOnlyMemory<byte> dek, CancellationToken ct);

    /// <summary>
    /// Decrypts and returns the plaintext. Caller is responsible for zeroing the returned buffer.
    /// Returns null if no token exists for the user+provider. Throws
    /// <see cref="System.Security.Cryptography.AuthenticationTagMismatchException"/> if the supplied DEK is wrong.
    /// </summary>
    Task<byte[]?> DecryptAsync(Guid userId, string provider, ReadOnlyMemory<byte> dek, CancellationToken ct);

    Task RemoveAsync(Guid userId, string provider, CancellationToken ct);

    Task<IReadOnlyList<ProviderTokenSummary>> ListAsync(Guid userId, CancellationToken ct);
}
