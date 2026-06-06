namespace ThanyMarcus.Portal.Api.Features.Provisioning;

public static class SagaKinds
{
    public const string Create  = "create";
    public const string Destroy = "destroy";
    public const string Cancel  = "cancel";
    public const string Migrate = "migrate";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Create, Destroy, Cancel, Migrate };
}
