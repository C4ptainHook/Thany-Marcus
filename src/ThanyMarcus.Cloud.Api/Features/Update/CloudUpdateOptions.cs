namespace ThanyMarcus.Cloud.Api.Features.Update;

public sealed class CloudUpdateOptions
{
    public const string SectionName = "Update";

    public string StateDir { get; set; } = "/mnt/thany-data/cloud-state";
}
