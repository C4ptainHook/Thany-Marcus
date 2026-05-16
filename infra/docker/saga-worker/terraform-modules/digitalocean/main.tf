locals {
  resource_name       = "thany-${substr(var.cloud_id, 0, 8)}"
  tags                = ["thany-marcus", "cloud-id-${var.cloud_id}", "managed-by-portal"]
  portal_callback_url = "${var.portal_url}/api/clouds/${var.cloud_id}/callback"
}

resource "digitalocean_volume" "data" {
  name                    = "${local.resource_name}-data"
  region                  = var.region
  size                    = var.volume_size_gb
  initial_filesystem_type = "ext4"
  description             = "Artifact + Postgres storage for thany-marcus cloud ${var.cloud_id}"
  tags                    = local.tags
}

resource "digitalocean_droplet" "cloud" {
  name       = local.resource_name
  region     = var.region
  size       = var.size
  image      = var.image_slug
  ipv6       = false
  monitoring = true
  tags       = local.tags

  user_data = templatefile("${path.module}/cloud-init.yaml.tpl", {
    cloud_id            = var.cloud_id
    hostname            = var.hostname
    enrollment_token    = var.enrollment_token
    portal_callback_url = local.portal_callback_url
    le_email            = var.le_email
    le_acme_ca          = var.le_acme_ca
    image_tag           = var.image_tag
    admin_user          = var.admin_user
    ssh_public_key      = var.ssh_public_key
    compose_url         = var.compose_url
    caddyfile_url       = var.caddyfile_url
    nginx_conf_url      = var.nginx_conf_url
    timezone            = var.timezone
  })

  lifecycle {
    ignore_changes = [user_data]
  }
}

resource "digitalocean_volume_attachment" "data" {
  droplet_id = digitalocean_droplet.cloud.id
  volume_id  = digitalocean_volume.data.id
}

resource "digitalocean_firewall" "cloud" {
  name        = "${local.resource_name}-fw"
  droplet_ids = [digitalocean_droplet.cloud.id]
  tags        = local.tags

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

  outbound_rule {
    protocol              = "icmp"
    destination_addresses = ["0.0.0.0/0", "::/0"]
  }
}
