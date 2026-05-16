variable "cluster_endpoint" {
  description = "Kubernetes cluster endpoint from infrastructure module"
  type        = string
}

variable "cluster_token" {
  description = "Kubernetes cluster token from infrastructure module"
  type        = string
  sensitive   = true
}

variable "cluster_ca_certificate" {
  description = "Kubernetes cluster CA certificate from infrastructure module"
  type        = string
  sensitive   = true
}

variable "certbot_email" {
  description = "Email for Let's Encrypt certificate notifications"
  type        = string
  sensitive   = true
}
