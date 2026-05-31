using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Passkey;

/// <summary>
/// Drives the endpoints without a real authenticator. Option generation is delegated to a
/// real Fido2 instance (deterministic, no crypto), while attestation/assertion verification
/// is controllable: set the callbacks to return a canned result, or leave them null to make
/// verification throw (simulating an invalid attestation/assertion).
/// </summary>
public sealed class FakeFido2 : IFido2
{
    private readonly Fido2 inner = new(new Fido2Configuration
    {
        ServerDomain = "thany.click",
        ServerName = "Thany-Marcus Portal",
        Origins = new HashSet<string> { "https://thany.click" },
    });

    public Func<MakeNewCredentialParams, RegisteredPublicKeyCredential>? OnMakeNewCredential { get; set; }
    public Func<MakeAssertionParams, VerifyAssertionResult>? OnMakeAssertion { get; set; }

    public CredentialCreateOptions RequestNewCredential(RequestNewCredentialParams requestNewCredentialParams)
        => inner.RequestNewCredential(requestNewCredentialParams);

    public AssertionOptions GetAssertionOptions(GetAssertionOptionsParams getAssertionOptionsParams)
        => inner.GetAssertionOptions(getAssertionOptionsParams);

    public Task<RegisteredPublicKeyCredential> MakeNewCredentialAsync(
        MakeNewCredentialParams makeNewCredentialParams, CancellationToken cancellationToken = default)
        => OnMakeNewCredential is { } f
            ? Task.FromResult(f(makeNewCredentialParams))
            : throw new Fido2VerificationException("attestation rejected by fake");

    public Task<VerifyAssertionResult> MakeAssertionAsync(
        MakeAssertionParams makeAssertionParams, CancellationToken cancellationToken = default)
        => OnMakeAssertion is { } f
            ? Task.FromResult(f(makeAssertionParams))
            : throw new Fido2VerificationException("assertion rejected by fake");
}

public static class FakeFido2Extensions
{
    public static WebApplicationFactory<Program> WithFido2(
        this WebApplicationFactory<Program> factory, IFido2 fido2)
        => factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IFido2>();
            s.AddScoped(_ => fido2);
        }));
}
