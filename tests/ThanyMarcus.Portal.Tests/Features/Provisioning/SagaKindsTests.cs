using Shouldly;
using ThanyMarcus.Portal.Api.Features.Provisioning;

namespace ThanyMarcus.Portal.Tests.Features.Provisioning;

public sealed class SagaKindsTests
{
    [Fact]
    public void Constants_match_persisted_string_values()
    {
        SagaKinds.Create.ShouldBe("create");
        SagaKinds.Destroy.ShouldBe("destroy");
        SagaKinds.Cancel.ShouldBe("cancel");
    }

    [Fact]
    public void All_contains_known_kinds()
    {
        SagaKinds.All.ShouldBe(new[] { SagaKinds.Create, SagaKinds.Destroy, SagaKinds.Cancel }, ignoreOrder: true);
    }
}
