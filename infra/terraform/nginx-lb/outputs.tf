output "droplet_ip" {
  value = digitalocean_droplet.nginx_lb.ipv4_address
}

output "floating_ip" {
  value = digitalocean_floating_ip.nginx_lb.ip_address
}

output "droplet_id" {
  value = digitalocean_droplet.nginx_lb.id
}

output "fqdn" {
  value = local.fqdn
}

output "ssh_connect" {
  value = "ssh ${var.linux_admin_user}@${digitalocean_floating_ip.nginx_lb.ip_address}"
}

output "nginx_status_url" {
  value = "http://${digitalocean_droplet.nginx_lb.ipv4_address}:8080/nginx_status"
}

output "prometheus_exporter_url" {
  value = "http://${digitalocean_droplet.nginx_lb.ipv4_address}:9113/metrics"
}

output "dns_record" {
  value = "Add DNS A record: ${var.subdomain} → ${digitalocean_floating_ip.nginx_lb.ip_address}"
}

output "urls" {
  value = {
    http  = "http://${local.fqdn}"
    https = "https://${local.fqdn}"
  }
}
