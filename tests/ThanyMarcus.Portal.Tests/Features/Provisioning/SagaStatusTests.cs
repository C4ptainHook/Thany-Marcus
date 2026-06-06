using Shouldly;
using ThanyMarcus.Portal.Api.Features.Provisioning;

namespace ThanyMarcus.Portal.Tests.Features.Provisioning;

public sealed class SagaStatusTests
{
    [Fact]
    public void Terminal_includes_failed_destroy()
    {
        SagaStatus.Terminal.ShouldContain(SagaStatus.FailedDestroy);
        SagaStatus.IsTerminal(SagaStatus.FailedDestroy).ShouldBeTrue();
    }

    [Fact]
    public void Destroying_is_a_transient_entry_status_not_terminal()
    {
        SagaStatus.Terminal.ShouldNotContain(SagaStatus.Destroying);
        SagaStatus.IsTerminal(SagaStatus.Destroying).ShouldBeFalse();
        SagaStatus.Destroying.ShouldBe("destroying");
    }

    [Fact]
    public void Failed_destroy_string_value_is_stable()
    {
        SagaStatus.FailedDestroy.ShouldBe("failed_destroy");
    }

    [Fact]
    public void Terminal_set_membership_is_exactly_the_known_terminal_states()
    {
        SagaStatus.Terminal.ShouldBe(new[]
        {
            SagaStatus.Succeeded,
            SagaStatus.FailedTf,
            SagaStatus.FailedDns,
            SagaStatus.FailedCallback,
            SagaStatus.FailedCert,
            SagaStatus.FailedPluginToken,
            SagaStatus.FailedDestroy,
            SagaStatus.FailedMintingSpaces,
            SagaStatus.Cancelled,
            SagaStatus.RolledBack,
            SagaStatus.MigrateSucceeded,
            SagaStatus.FailedMigrate,
            SagaStatus.MigrateRolledBack,
        }, ignoreOrder: true);
    }

    [Fact]
    public void Existing_create_side_terminals_remain_terminal()
    {
        SagaStatus.IsTerminal(SagaStatus.Succeeded).ShouldBeTrue();
        SagaStatus.IsTerminal(SagaStatus.FailedTf).ShouldBeTrue();
        SagaStatus.IsTerminal(SagaStatus.FailedDns).ShouldBeTrue();
        SagaStatus.IsTerminal(SagaStatus.FailedCallback).ShouldBeTrue();
        SagaStatus.IsTerminal(SagaStatus.FailedCert).ShouldBeTrue();
        SagaStatus.IsTerminal(SagaStatus.Cancelled).ShouldBeTrue();
        SagaStatus.IsTerminal(SagaStatus.RolledBack).ShouldBeTrue();
    }
}
