using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;

namespace ThanyMarcus.Portal.Api.Features.Provisioning.Providers;

public static class ProviderTerraformCatalog
{
    public static ProviderTerraformLayout Get(string providerKey) => providerKey switch
    {
        KnownProviders.Stub         => Stub,
        KnownProviders.DigitalOcean => DigitalOcean,
        _ => throw new InvalidOperationException(
            $"No terraform layout configured for provider '{providerKey}'"),
    };

    private static readonly ProviderTerraformLayout Stub = new(
        ModuleFolderName: KnownProviders.Stub,
        RequiresCloudInitTfvars: false,
        RequiredProvidersBlock: string.Empty,
        ProviderBlock: string.Empty,
        ExtraModuleArgs: string.Empty,
        ExtraRootVars: string.Empty);

    private static readonly ProviderTerraformLayout DigitalOcean = new(
        ModuleFolderName: KnownProviders.DigitalOcean,
        RequiresCloudInitTfvars: true,
        RequiredProvidersBlock:
            """
              required_providers {
                digitalocean = {
                  source  = "digitalocean/digitalocean"
                  version = "~> 2.0"
                }
              }
            """,
        ProviderBlock:
            """

            provider "digitalocean" {
              token = var.provider_token
            }
            """,
        ExtraModuleArgs:
            """

              enrollment_token       = var.enrollment_token
              ghcr_pat               = var.ghcr_pat
              portal_url             = var.portal_url
              le_email               = var.le_email
              le_acme_ca             = var.le_acme_ca
              image_tag              = var.image_tag
              admin_user             = var.admin_user
              ssh_public_key         = var.ssh_public_key
              timezone               = var.timezone
              ollama_vision_pull_tag = var.ollama_vision_pull_tag
              ollama_text_pull_tag   = var.ollama_text_pull_tag
              ollama_text_image_tag  = var.ollama_text_image_tag
            """,
        ExtraRootVars:
            """

            variable "provider_token" {
              type      = string
              sensitive = true
              default   = ""
            }
            variable "enrollment_token" {
              type      = string
              sensitive = true
              default   = ""
            }
            variable "ghcr_pat" {
              type      = string
              sensitive = true
              default   = ""
            }
            variable "portal_url"     { type = string }
            variable "le_email"       { type = string }
            variable "le_acme_ca" {
              type    = string
              default = ""
            }
            variable "image_tag"  { type = string }
            variable "admin_user" {
              type    = string
              default = "thanyadmin"
            }
            variable "ssh_public_key" {
              type    = string
              default = ""
            }
            variable "timezone" {
              type    = string
              default = "Etc/UTC"
            }
            variable "ollama_vision_pull_tag" {
              type    = string
              default = "qwen3-vl:4b"
            }
            variable "ollama_text_pull_tag" {
              type    = string
              default = "qwen3:1.7b-q4_K_M"
            }
            variable "ollama_text_image_tag" {
              type    = string
              default = "0.24.0"
            }
            """);
}
