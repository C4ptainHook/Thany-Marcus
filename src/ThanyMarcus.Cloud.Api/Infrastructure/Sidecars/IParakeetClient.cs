namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public interface IParakeetClient
{
    Task<string> TranscribeAsync(string storageKey, CancellationToken ct);
}
