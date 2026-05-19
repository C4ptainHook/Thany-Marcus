using ThanyMarcus.Cloud.Api.Features.Settings;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm;

public interface ILlmClientFactory
{
    ILlmClient Resolve(CloudSettings settings, out bool fallbackToSafe);
}
