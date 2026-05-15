using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using NodaTime;
using NodaTime.Testing;
using OtpNet;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth.Totp;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Totp;

public sealed class TotpServiceTests
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 5, 15, 12, 0);

    private static TotpService CreateService(IClock? clock = null, string appName = "test-app")
    {
        var dp = DataProtectionProvider.Create(appName);
        return new TotpService(dp, clock ?? new FakeClock(TestNow));
    }

    [Fact]
    public void GenerateSecret_returns_base32_string_decodable_to_20_bytes()
    {
        var svc = CreateService();
        var secret = svc.GenerateSecret();
        secret.ShouldNotBeNullOrWhiteSpace();
        var bytes = Base32Encoding.ToBytes(secret);
        bytes.Length.ShouldBe(20);
    }

    [Fact]
    public void GenerateSecret_returns_distinct_values()
    {
        var svc = CreateService();
        var a = svc.GenerateSecret();
        var b = svc.GenerateSecret();
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Verify_accepts_freshly_computed_code()
    {
        var clock = new FakeClock(TestNow);
        var svc = CreateService(clock);
        var secret = svc.GenerateSecret();
        var code = new OtpNet.Totp(Base32Encoding.ToBytes(secret))
            .ComputeTotp(TestNow.ToDateTimeUtc());

        svc.Verify(secret, code).ShouldBeTrue();
    }

    [Fact]
    public void Verify_rejects_code_from_two_steps_ago()
    {
        var clock = new FakeClock(TestNow);
        var svc = CreateService(clock);
        var secret = svc.GenerateSecret();
        var stale = new OtpNet.Totp(Base32Encoding.ToBytes(secret))
            .ComputeTotp(TestNow.Minus(Duration.FromSeconds(90)).ToDateTimeUtc());

        svc.Verify(secret, stale).ShouldBeFalse();
    }

    [Fact]
    public void Verify_rejects_code_from_two_steps_in_future()
    {
        var clock = new FakeClock(TestNow);
        var svc = CreateService(clock);
        var secret = svc.GenerateSecret();
        var future = new OtpNet.Totp(Base32Encoding.ToBytes(secret))
            .ComputeTotp(TestNow.Plus(Duration.FromSeconds(90)).ToDateTimeUtc());

        svc.Verify(secret, future).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void Verify_rejects_malformed_codes(string code)
    {
        var svc = CreateService();
        var secret = svc.GenerateSecret();
        svc.Verify(secret, code).ShouldBeFalse();
    }

    [Fact]
    public void Encrypt_then_Decrypt_round_trips_secret()
    {
        var svc = CreateService();
        var secret = svc.GenerateSecret();

        var (ct, nonce, tag) = svc.Encrypt(secret);
        ct.Length.ShouldBeGreaterThan(0);
        nonce.ShouldNotBeNull();
        tag.ShouldNotBeNull();

        svc.Decrypt(ct).ShouldBe(secret);
    }

    [Fact]
    public void Decrypt_with_different_application_name_throws()
    {
        var a = CreateService(appName: "app-a");
        var b = CreateService(appName: "app-b");
        var secret = a.GenerateSecret();
        var (ct, _, _) = a.Encrypt(secret);

        Should.Throw<CryptographicException>(() => b.Decrypt(ct));
    }

    [Fact]
    public void BuildQrPngDataUri_returns_valid_png_data_uri()
    {
        var svc = CreateService();
        var secret = svc.GenerateSecret();
        var uri = svc.BuildQrPngDataUri(secret, "alice@example.com");

        uri.ShouldStartWith("data:image/png;base64,");
        var base64 = uri["data:image/png;base64,".Length..];
        var bytes = Convert.FromBase64String(base64);
        bytes.Length.ShouldBeGreaterThan(8);
        bytes[0].ShouldBe((byte)0x89);
        bytes[1].ShouldBe((byte)'P');
        bytes[2].ShouldBe((byte)'N');
        bytes[3].ShouldBe((byte)'G');
    }
}
