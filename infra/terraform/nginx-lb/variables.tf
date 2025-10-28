variable "do_token" {
  description = "DigitalOcean Personal Access Token"
  type        = string
  sensitive   = true
}

variable "certbot_email" {
  type        = string
  description = "Email address for Let's Encrypt notifications"
  sensitive   = true
}

variable "name_prefix" {
  description = "Prefix for resource names"
  type        = string
  default     = "nginx-lb"
}

variable "region" {
  description = "DigitalOcean region"
  type        = string
  default     = "sfo3"
}

variable "size" {
  description = "Droplet size slug"
  type        = string
  default     = "s-2vcpu-2gb"
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

variable "vpc_id" {
  description = "VPC ID to attach the droplet (from backend)"
  type        = string
}

variable "backend_target" {
  description = "Backend target IP or hostname (Kubernetes ingress IP)"
  type        = string
}

variable "subdomain" {
  description = "Subdomain for nginx LB"
  type        = string
  default     = "nginx"
}

variable "domain_name" {
  description = "Base domain name"
  type        = string
  default     = "thany.click"
}

variable "linux_admin_user" {
  description = "Non-root admin user for the droplet"
  type        = string
  default     = "marcusadmin"
}

variable "enable_do_firewall" {
  description = "Create and attach a DO Cloud Firewall"
  type        = bool
  default     = true
}

variable "droplet_backups" {
  description = "Enable DO backups"
  type        = bool
  default     = false
}

variable "droplet_monitoring" {
  description = "Enable DO monitoring"
  type        = bool
  default     = true
}

variable "tags" {
  description = "Tags to apply to resources"
  type        = list(string)
  default     = ["nginx", "load-balancer"]
}

variable "basic_auth_users" {
  description = "Map of username to bcrypt password hash for Basic Auth"
  type        = map(string)
  default     = {}
  sensitive   = true
}

variable "rate_limit_req_per_sec" {
  description = "Rate limit requests per second per IP"
  type        = number
  default     = 30
}

variable "rate_limit_burst" {
  description = "Rate limit burst size"
  type        = number
  default     = 50
}

variable "connection_limit" {
  description = "Maximum concurrent connections per IP"
  type        = number
  default     = 10
}
