locals {
  fqdn                    = "${var.subdomain}.${var.domain_name}"
  selected_public_keys    = try([for k in data.digitalocean_ssh_keys.selected[0].ssh_keys : k.public_key], [])
  ssh_keys_all            = concat(var.ssh_pubkeys_raw, local.selected_public_keys)
  ssh_keys_block          = length(local.ssh_keys_all) > 0 ? join("\n", [for k in local.ssh_keys_all : "      - ${k}"]) : ""
  resolved_ssh_keys_count = length(local.selected_public_keys)
  basic_auth_htpasswd     = join("\n", [for user, hash in var.basic_auth_users : "${user}:${hash}"])
}

data "digitalocean_ssh_keys" "selected" {
  count = length(var.ssh_key_names) > 0 ? 1 : 0
  filter {
    key    = "name"
    values = var.ssh_key_names
  }
}

resource "random_id" "suffix" {
  byte_length = 2
}

resource "digitalocean_floating_ip" "nginx_lb" {
  region = var.region
}

resource "digitalocean_droplet" "nginx_lb" {
  name       = "${var.name_prefix}-${random_id.suffix.hex}"
  region     = var.region
  size       = var.size
  image      = var.image
  backups    = var.droplet_backups
  monitoring = var.droplet_monitoring
  ipv6       = true
  tags       = var.tags
  vpc_uuid   = var.vpc_id

  ssh_keys = length(var.ssh_key_names) > 0 ? [
    for k in data.digitalocean_ssh_keys.selected[0].ssh_keys : k.fingerprint
  ] : []

  user_data = templatefile("${path.module}/templates/cloudinit.tpl", {
    extra_ssh_keys         = local.ssh_keys_block
    admin_user             = var.linux_admin_user
    fqdn                   = local.fqdn
    backend_target         = var.backend_target
    certbot_email          = var.certbot_email
    basic_auth_htpasswd    = local.basic_auth_htpasswd
    rate_limit_req_per_sec = var.rate_limit_req_per_sec
    rate_limit_burst       = var.rate_limit_burst
    connection_limit       = var.connection_limit
  })

  lifecycle {
    ignore_changes = [user_data]
    precondition {
      condition     = local.resolved_ssh_keys_count > 0 || length(var.ssh_pubkeys_raw) > 0
      error_message = "No SSH keys resolved. Ensure ssh_key_names exist in your DO account or provide ssh_pubkeys_raw."
    }
  }

  depends_on = [digitalocean_floating_ip.nginx_lb]
}

resource "digitalocean_floating_ip_assignment" "nginx_lb_assign" {
  ip_address = digitalocean_floating_ip.nginx_lb.ip_address
  droplet_id = digitalocean_droplet.nginx_lb.id
  depends_on = [digitalocean_droplet.nginx_lb]
}

resource "digitalocean_firewall" "nginx_lb_fw" {
  count       = var.enable_do_firewall ? 1 : 0
  name        = "${var.name_prefix}-fw"
  droplet_ids = [digitalocean_droplet.nginx_lb.id]

  inbound_rule {
    protocol         = "tcp"
    port_range       = "22"
    source_addresses = ["0.0.0.0/0", "::/0"]
  }

  inbound_rule {
    protocol         = "tcp"
    port_range       = "80"
    source_addresses = ["0.0.0.0/0", "::/0"]
  }

  inbound_rule {
    protocol         = "tcp"
    port_range       = "443"
    source_addresses = ["0.0.0.0/0", "::/0"]
  }

  inbound_rule {
    protocol         = "tcp"
    port_range       = "8080"
    source_addresses = ["10.0.0.0/8"]
  }

  inbound_rule {
    protocol         = "tcp"
    port_range       = "9113"
    source_addresses = ["10.0.0.0/8"]
  }

  outbound_rule {
    protocol              = "icmp"
    destination_addresses = ["0.0.0.0/0", "::/0"]
  }

  outbound_rule {
    protocol              = "tcp"
    port_range            = "1-65535"
    destination_addresses = ["0.0.0.0/0", "::/0"]
  }

  outbound_rule {
    protocol              = "udp"
    port_range            = "1-65535"
    destination_addresses = ["0.0.0.0/0", "::/0"]
  }
}
