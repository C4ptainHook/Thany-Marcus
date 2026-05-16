namespace ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;

public sealed class ProviderTokenAlreadyExistsException(Guid userId, string provider)
    : InvalidOperationException($"Provider token already exists for user {userId} and provider '{provider}'")
{
    public Guid   UserId   { get; } = userId;
    public string Provider { get; } = provider;
}
