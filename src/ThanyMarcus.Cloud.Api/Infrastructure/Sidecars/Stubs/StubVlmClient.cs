namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Stubs;

public sealed class StubVlmClient : IVlmClient
{
    // FORK: stub; real impl lands in handoffs #4-#6
    public async Task<string> DescribeImageAsync(string storageKey, CancellationToken ct)
    {
        await Task.Delay(50, ct);
        return $"[stub VLM description for {storageKey}; model=stub-v1]";
    }
}
