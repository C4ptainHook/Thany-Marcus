output "droplet_ip" {
  value = digitalocean_droplet.vm.ipv4_address
}

output "ssh_connect" {
  value = "ssh ${var.linux_admin_user}@${digitalocean_droplet.vm.ipv4_address}"
}

output "droplet_id" {
  value = digitalocean_droplet.vm.id
}

output "droplet_ipv6" {
  value = digitalocean_droplet.vm.ipv6_address
}
    