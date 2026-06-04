using System.Diagnostics.CodeAnalysis;

namespace ThanyMarcus.Portal.Api.Features.Provisioning.Providers;

public interface IProvisioningProviderRegistry
{
    IProvisioningProvider Resolve(string key);

    bool TryGet(string key, [NotNullWhen(true)] out IProvisioningProvider? provider);

    IReadOnlyList<IProvisioningProvider> All { get; }
}

public sealed class ProvisioningProviderRegistry : IProvisioningProviderRegistry
{
    private readonly Dictionary<string, IProvisioningProvider> _byKey;

    public ProvisioningProviderRegistry(IEnumerable<IProvisioningProvider> providers)
    {
        _byKey = providers.ToDictionary(p => p.Key, StringComparer.Ordinal);
        All = [.. _byKey.Values];
    }

    public IReadOnlyList<IProvisioningProvider> All { get; }

    public IProvisioningProvider Resolve(string key) =>
        _byKey.TryGetValue(key, out var provider)
            ? provider
            : throw new InvalidOperationException($"No provisioning provider registered for '{key}'");

    public bool TryGet(string key, [NotNullWhen(true)] out IProvisioningProvider? provider) =>
        _byKey.TryGetValue(key, out provider);
}
