using Shouldly;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Handlers;

public sealed class ToPostgresUrlTests
{
    [Fact]
    public void Plain_credentials_render_with_sslmode_disable_when_ssl_not_required()
    {
        var url = TfPlanningHandler.ToPostgresUrl(
            "Host=postgres;Port=5432;Username=postgres;Password=postgres;Database=portal_dev");

        url.ShouldBe("postgres://postgres:postgres@postgres:5432/portal_dev?sslmode=disable");
    }

    [Fact]
    public void SslMode_Require_renders_as_sslmode_require()
    {
        var url = TfPlanningHandler.ToPostgresUrl(
            "Host=db;Port=5432;Username=u;Password=p;Database=d;SSL Mode=Require");

        url.ShouldEndWith("?sslmode=require");
    }

    [Fact]
    public void Password_with_special_chars_is_url_encoded()
    {
        var url = TfPlanningHandler.ToPostgresUrl(
            "Host=db;Port=5432;Username=admin@home;Password=p@ss:w/ord;Database=d");

        // '@' → %40, ':' → %3A, '/' → %2F
        url.ShouldContain("admin%40home");
        url.ShouldContain("p%40ss%3Aw%2Ford");
    }

    [Fact]
    public void Default_port_5432_used_when_unspecified()
    {
        var url = TfPlanningHandler.ToPostgresUrl(
            "Host=db;Username=u;Password=p;Database=d");

        url.ShouldContain("@db:5432/");
    }

    [Fact]
    public void Empty_username_renders_with_empty_userinfo()
    {
        var url = TfPlanningHandler.ToPostgresUrl(
            "Host=db;Port=5432;Username=;Password=;Database=d");

        url.ShouldBe("postgres://:@db:5432/d?sslmode=disable");
    }
}
