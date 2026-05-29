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
  description = "Fully-qualified hostname (e.g. abc12345.thany.click). Used for droplet name + cloud-init bootstrap + nginx/LE config."
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

variable "le_email" {
  description = "Let's Encrypt account email used by certbot's ACME registration."
  type        = string
}

variable "le_acme_ca" {
  description = "Optional ACME CA directory URL. Empty string = production LE. Use the staging URL during smoke tests to avoid LE rate limits."
  type        = string
  default     = ""
}

variable "image_tag" {
  description = "Container image tag pinned per release; written to the cloud .env so docker-compose can pull a stable build."
  type        = string
}

variable "admin_user" {
  description = "Non-root sudo user created by cloud-init."
  type        = string
  default     = "thanyadmin"
}

variable "ssh_public_key" {
  description = "OpenSSH public key written to the admin user's authorized_keys. Empty string is allowed for unattended provisioning where SSH access is not required."
  type        = string
  default     = ""
}

variable "timezone" {
  description = "System timezone passed to timedatectl set-timezone."
  type        = string
  default     = "Etc/UTC"
}

variable "ollama_vision_pull_tag" {
  description = "Ollama model tag to pre-pull on the vision server. Must match the tag the VlmWorker requests at runtime. Default targets Qwen3-VL 4B — fits in 6g mem_limit with 4g swap; text container unloads at idle (OLLAMA_KEEP_ALIVE=0) to free RAM during vision calls."
  type        = string
  default     = "qwen3-vl:4b"
}

variable "ollama_text_pull_tag" {
  description = "Ollama model tag to pre-pull on the text server. Drives routing + entity extraction + synthesis. Default targets Qwen3 1.7B Q4_K_M — instruction-tuned for structured/JSON output."
  type        = string
  default     = "qwen3:1.7b-q4_K_M"
}

variable "ollama_text_image_tag" {
  description = "Tag for the upstream ollama/ollama image used by both vision and text servers."
  type        = string
  default     = "0.24.0"
}
