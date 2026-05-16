using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ThanyMarcus.Portal.Api.Features.Auth.Captcha;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Captcha;

internal static class TurnstileTestExtensions
{
    public const string TestSiteKey = "1x00000000000000000000AA";
    public const string TestSecretKey = "1x0000000000000000000000000000000AA";

    public static IDictionary<string, string?> Enabled(this Dictionary<string, string?> overrides, int failureThreshold = 5, int trackerTtlSeconds = 900)
    {
        overrides["Turnstile:SiteKey"] = TestSiteKey;
        overrides["Turnstile:SecretKey"] = TestSecretKey;
        overrides["Turnstile:FailureThreshold"] = failureThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
        overrides["Turnstile:TrackerTtlSeconds"] = trackerTtlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return overrides;
    }

    public static WebApplicationFactory<Program> WithTurnstileValidator(
        this WebApplicationFactory<Program> factory,
        StubTurnstileValidator stub)
        => factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ITurnstileValidator>();
            s.AddSingleton<ITurnstileValidator>(stub);
        }));
}
