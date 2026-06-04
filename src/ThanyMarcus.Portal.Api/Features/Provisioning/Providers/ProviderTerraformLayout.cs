namespace ThanyMarcus.Portal.Api.Features.Provisioning.Providers;

public sealed record ProviderTerraformLayout(
    string ModuleFolderName,
    bool RequiresCloudInitTfvars,
    string RequiredProvidersBlock,
    string ProviderBlock,
    string ExtraModuleArgs,
    string ExtraRootVars);
