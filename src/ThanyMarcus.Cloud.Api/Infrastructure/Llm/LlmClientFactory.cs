using System.Text;
using LlmTornado;
using LlmTornado.Code;
using LlmTornado.Microsoft.Extensions.AI;
using Microsoft.AspNetCore.DataProtection;
using ThanyMarcus.Cloud.Api.Features.Settings;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm;

public sealed class LlmClientFactory : ILlmClientFactory
{
    private readonly IDataProtectionProvider dataProtection;
    private readonly LlmOptions options;

    public LlmClientFactory(IDataProtectionProvider dataProtection, LlmOptions options)
    {
        this.dataProtection = dataProtection;
        this.options = options;
    }

    public ILlmClient Resolve(CloudSettings settings)
    {
        switch (settings.LlmMode)
        {
            case LlmModes.UnsafeAnthropic:
            {
                var apiKey = UnprotectKey(settings.EncryptedExternalApiKey)
                    ?? throw new InvalidOperationException(
                        "llm_mode=unsafe_anthropic but no external API key configured.");
                var model = string.IsNullOrWhiteSpace(settings.LlmModel) ? options.DefaultAnthropicModel : settings.LlmModel;
                var tornado = new TornadoApi([new ProviderAuthentication(LLmProviders.Anthropic, apiKey)]);
                var chat = tornado.AsChatClient(model);
                return new ChatClientLlm(chat, LlmModes.UnsafeAnthropic, model, options);
            }

            case LlmModes.UnsafeOpenAi:
            {
                var apiKey = UnprotectKey(settings.EncryptedExternalApiKey)
                    ?? throw new InvalidOperationException(
                        "llm_mode=unsafe_openai but no external API key configured.");
                var model = string.IsNullOrWhiteSpace(settings.LlmModel) ? options.DefaultOpenAiModel : settings.LlmModel;
                var tornado = new TornadoApi([new ProviderAuthentication(LLmProviders.OpenAi, apiKey)]);
                var chat = tornado.AsChatClient(model);
                return new ChatClientLlm(chat, LlmModes.UnsafeOpenAi, model, options);
            }

            case LlmModes.Safe:
            default:
                return new NoOpLlmClient();
        }
    }

    private string? UnprotectKey(byte[]? ciphertext)
    {
        if (ciphertext is null || ciphertext.Length == 0) return null;
        var protector = dataProtection.CreateProtector(AdminSettingsEndpoints.DataProtectionPurpose);
        return Encoding.UTF8.GetString(protector.Unprotect(ciphertext));
    }
}
