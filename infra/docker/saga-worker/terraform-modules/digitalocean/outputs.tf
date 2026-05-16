output "ip" {
  description = "Droplet public IPv4 address. PORTAL-007's WorkspaceLayout rendering forwards this as the root module's ip output, which DnsCreatingHandler reads to set up the Cloudflare A-record."
  value       = digitalocean_droplet.cloud.ipv4_address
}

output "droplet_id" {
  description = "DO droplet numeric id. Surfaced for debugging; not consumed by the saga."
  value       = digitalocean_droplet.cloud.id
}

output "volume_id" {
  description = "DO volume id. Surfaced for debugging; not consumed by the saga."
  value       = digitalocean_volume.data.id
}
