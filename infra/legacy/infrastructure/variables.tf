variable "do_token" {
  description = "DigitalOcean Personal Access Token"
  type        = string
  sensitive   = true
}

variable "certbot_email" {
  type        = string
  description = "Email address for Let's Encrypt notifications."
  sensitive   = true
}

variable "grafana_password" {
  type        = string
  description = "Grafana admin password"
  sensitive   = true
  default     = "admin"
}

variable "name_prefix" {
  description = "Prefix for resource names"
  type        = string
  default     = "knowledge-hub"
  validation {
    condition     = can(regex("^[a-z0-9-]+$", var.name_prefix))
    error_message = "The name_prefix can only contain lowercase letters, numbers, and hyphens."
  }
}

variable "linux_admin_user" {
  description = "The username for the non-root admin user to be created on the Droplet."
  type        = string
  default     = "marcusadmin"
  validation {
    condition     = can(regex("^[a-z_][a-z0-9_-]*[$]?$", var.linux_admin_user))
    error_message = "Invalid Linux username format."
  }
}

variable "region" {
  description = "DigitalOcean region"
  type        = string
  default     = "sfo3"
  validation {
    condition     = contains(["nyc1", "nyc3", "sfo2", "sfo3", "ams3", "fra1", "lon1", "sgp1", "blr1", "tor1"], var.region)
    error_message = "The specified region is not a valid DigitalOcean region slug."
  }
}

variable "size" {
  description = "Droplet size slug"
  type        = string
  default     = "s-1vcpu-1gb"
}

variable "image" {
  description = "Droplet image slug"
  type        = string
  default     = "ubuntu-24-04-x64"
}

variable "ssh_key_names" {
  description = "List of existing DO SSH key names to attach"
  type        = list(string)
  default     = []
}

variable "ssh_pubkeys_raw" {
  description = "Optional list of raw SSH public keys to inject via cloud-init"
  type        = list(string)
  default     = []
}

variable "enable_do_firewall" {
  description = "Create and attach a DO Cloud Firewall"
  type        = bool
  default     = true
}

variable "allow_additional_tcp_ports" {
  description = "Extra inbound TCP ports to allow (UFW and DO firewall)"
  type        = list(number)
  default     = []
}

variable "droplet_backups" {
  description = "Enable DO backups (extra cost)"
  type        = bool
  default     = false
}

variable "droplet_monitoring" {
  description = "Enable DO monitoring"
  type        = bool
  default     = true
}

variable "tags" {
  description = "Optional tags to apply to resources"
  type        = list(string)
  default     = ["knowledge-hub", "n8n", "obsidian"]
}

variable "n8n_root_dir" {
  description = "Host directory for n8n configuration and persistent data"
  type        = string
  default     = "/opt/n8n"
}

variable "n8n_port" {
  description = "Host port to expose n8n UI/API"
  type        = number
  default     = 5678
}

variable "n8n_version" {
  description = "n8n Docker image tag (e.g., 'latest' or a specific version)"
  type        = string
  default     = "latest"
}

variable "n8n_timezone" {
  description = "Timezone for n8n (IANA format, e.g., 'UTC', 'America/Los_Angeles')"
  type        = string
  default     = "UTC"
}

variable "enable_kubernetes" {
  description = "Enable Kubernetes cluster provisioning"
  type        = bool
  default     = false
}

variable "kubernetes_version" {
  description = "Kubernetes version for DOKS cluster"
  type        = string
  default     = "1.33.1-do.5"
}

variable "kubernetes_node_size" {
  description = "Node size for Kubernetes workers"
  type        = string
  default     = "s-4vcpu-8gb"
}

variable "kubernetes_node_count" {
  description = "Initial number of Kubernetes worker nodes"
  type        = number
  default     = 1
}

variable "kubernetes_autoscale" {
  description = "Enable Kubernetes node autoscaling"
  type        = bool
  default     = false
}

variable "kubernetes_min_nodes" {
  description = "Minimum nodes for autoscaling"
  type        = number
  default     = 2
}

variable "kubernetes_max_nodes" {
  description = "Maximum nodes for autoscaling"
  type        = number
  default     = 5
}
