namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public interface IVlmClient
{
    Task<string> DescribeImageAsync(string storageKey, CancellationToken ct);
}
