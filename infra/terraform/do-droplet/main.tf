locals {
  name                    = "${var.name_prefix}-droplet"
  selected_public_keys    = try([for k in data.digitalocean_ssh_keys.selected[0].ssh_keys : k.public_key], [])
  ssh_keys_all            = concat(var.ssh_pubkeys_raw, local.selected_public_keys)
  ssh_keys_block          = length(local.ssh_keys_all) > 0 ? join("\n", [for k in local.ssh_keys_all : "      - ${k}"]) : ""
  resolved_ssh_keys_count = length(local.selected_public_keys)
  allow_tcp_ports         = distinct(concat(var.allow_additional_tcp_ports, ["22", "80", "443"]))
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

resource "digitalocean_floating_ip" "n8n_ip" {
  region = var.region
}

resource "digitalocean_droplet" "vm" {
  name       = "${local.name}-${random_id.suffix.hex}"
  region     = var.region
  size       = var.size
  image      = var.image
  backups    = var.droplet_backups
  monitoring = var.droplet_monitoring
  ipv6       = true
  tags       = var.tags

  ssh_keys = length(var.ssh_key_names) > 0 ? [
    for k in data.digitalocean_ssh_keys.selected[0].ssh_keys : k.fingerprint
  ] : []

  user_data = templatefile("${path.module}/templates/cloudinit.tpl", {
    extra_ssh_keys = local.ssh_keys_block
    additional_tcp = local.allow_tcp_ports
    admin_user     = var.linux_admin_user
    n8n_root_dir   = var.n8n_root_dir
    n8n_port       = var.n8n_port
    n8n_version    = var.n8n_version
    n8n_timezone   = var.n8n_timezone
    domain_name    = var.domain_name
    certbot_email  = var.certbot_email
  })

  lifecycle {
    ignore_changes = [user_data]
    precondition {
      condition     = local.resolved_ssh_keys_count > 0 || length(var.ssh_pubkeys_raw) > 0
      error_message = "No SSH keys resolved. Ensure ssh_key_names exist in your DO account or provide ssh_pubkeys_raw."
    }
  }
  depends_on = [digitalocean_floating_ip.n8n_ip]
}

resource "digitalocean_floating_ip_assignment" "n8n_ip_assign" {
  ip_address = digitalocean_floating_ip.n8n_ip.ip_address
  droplet_id = digitalocean_droplet.vm.id
  depends_on = [digitalocean_droplet.vm]
}

resource "digitalocean_firewall" "fw" {
  count       = var.enable_do_firewall ? 1 : 0
  name        = "${var.name_prefix}-fw"
  droplet_ids = [digitalocean_droplet.vm.id]

  dynamic "inbound_rule" {
    for_each = local.allow_tcp_ports
    content {
      protocol         = "tcp"
      port_range       = tostring(inbound_rule.value)
      source_addresses = ["0.0.0.0/0", "::/0"]
    }
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

