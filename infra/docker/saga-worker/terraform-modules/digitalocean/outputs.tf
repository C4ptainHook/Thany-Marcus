output "ip" {
  description = "Reserved IP bound to the droplet. The domain A-record points here permanently; blue-green cutover reassigns this reserved IP to the green droplet without a DNS change."
  value       = digitalocean_reserved_ip.cloud.ip_address
}

output "reserved_ip" {
  description = "Reserved IP address for this cloud. Stamped onto cloud.VmIp and reassigned old->green during a blue-green migration."
  value       = digitalocean_reserved_ip.cloud.ip_address
}

output "droplet_ipv4" {
  description = "Droplet's own ephemeral public IPv4 (pre-reserved-IP). Surfaced for debugging only."
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

output "bucket_name" {
  description = "DO Spaces bucket name for this cloud's artifact storage. Stamped onto attachment rows as storage_bucket."
  value       = digitalocean_spaces_bucket.artifacts.name
}

output "bucket_region" {
  description = "DO Spaces bucket region slug; matches the droplet region."
  value       = digitalocean_spaces_bucket.artifacts.region
}

output "bucket_endpoint" {
  description = "DO Spaces region endpoint (e.g. https://fra1.digitaloceanspaces.com). Cloud.Api signs requests against this."
  value       = local.bucket_endpoint
}

output "access_key_id" {
  description = "Scoped Spaces access key id. Written into the cloud-api compose env; sensitive."
  value       = digitalocean_spaces_key.artifacts.access_key
  sensitive   = true
}

output "access_key_secret" {
  description = "Scoped Spaces access key secret. Written into the cloud-api compose env; sensitive."
  value       = digitalocean_spaces_key.artifacts.secret_key
  sensitive   = true
}
