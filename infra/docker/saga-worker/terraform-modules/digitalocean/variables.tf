variable "cloud_id" {
  description = "Portal-side UUID for this cloud. Stamped as a tag on every resource for cross-system correlation."
  type        = string
}

variable "region" {
  description = "DO region slug (e.g. fra1, nyc3). Validated portal-side; see Region validation contract."
  type        = string
}

variable "size" {
  description = "DO droplet size slug. Default per DEC-001: s-4vcpu-16gb."
  type        = string
}

variable "hostname" {
  description = "Fully-qualified hostname (e.g. abc12345.thany.click). Used for droplet name + cloud-init bootstrap + Caddy/LE config."
  type        = string
}

variable "enrollment_token" {
  description = "One-shot token the cloud uses to register itself with the portal on first boot. Passed into cloud-init via TF_VAR_enrollment_token; never written to tfvars."
  type        = string
  sensitive   = true
}

variable "ghcr_pat" {
  description = "GitHub Container Registry PAT for image pulls. Public images don't require auth, but a PAT lifts the unauthenticated rate-limit. Passed via TF_VAR_ghcr_pat."
  type        = string
  sensitive   = true
}

variable "portal_url" {
  description = "Base URL of the portal API (e.g. https://api.thany.click). Cloud-init POSTs the cloud_admin_token here on first boot."
  type        = string
}

variable "volume_size_gb" {
  description = "Attached volume size in GB. Default 50 — fits ~150-200 artifacts including audio/images for thesis scope."
  type        = number
  default     = 50
}

variable "image_slug" {
  description = "DO image slug. Pinned to ubuntu-24-04-x64; a follow-up re-pins when 26.04 LTS ships."
  type        = string
  default     = "ubuntu-24-04-x64"
}
