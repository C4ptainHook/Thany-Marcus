locals {
  resource_name       = "thany-${substr(var.cloud_id, 0, 8)}"
  bucket_name         = "thany-cloud-${substr(var.cloud_id, 0, 8)}"
  bucket_endpoint     = "https://${var.region}.digitaloceanspaces.com"
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
    cloud_id              = var.cloud_id
    hostname              = var.hostname
    enrollment_token      = var.enrollment_token
    portal_callback_url   = local.portal_callback_url
    le_email              = var.le_email
    le_acme_ca            = var.le_acme_ca
    image_tag             = var.image_tag
    admin_user            = var.admin_user
    ssh_public_key        = var.ssh_public_key
    timezone              = var.timezone
    storage_provider      = "s3"
    storage_endpoint      = local.bucket_endpoint
    storage_region        = var.region
    storage_bucket        = digitalocean_spaces_bucket.artifacts.name
    storage_access_key_id = digitalocean_spaces_key.artifacts.access_key
    storage_access_secret = digitalocean_spaces_key.artifacts.secret_key
    ollama_pull_tag       = var.ollama_pull_tag
    ollama_image_tag      = var.ollama_image_tag
  })

  lifecycle {
    ignore_changes = [user_data]
  }
}

resource "digitalocean_volume_attachment" "data" {
  droplet_id = digitalocean_droplet.cloud.id
  volume_id  = digitalocean_volume.data.id
}

resource "digitalocean_spaces_bucket" "artifacts" {
  name          = local.bucket_name
  region        = var.region
  acl           = "private"
  force_destroy = true
}

resource "digitalocean_spaces_bucket_cors_configuration" "artifacts" {
  bucket = digitalocean_spaces_bucket.artifacts.id
  region = digitalocean_spaces_bucket.artifacts.region

  cors_rule {
    allowed_methods = ["PUT", "GET"]
    allowed_origins = ["https://app.obsidian.md", "app://obsidian.md"]
    allowed_headers = ["Content-Type", "Content-MD5", "x-amz-*"]
    expose_headers  = ["ETag"]
    max_age_seconds = 3000
  }
}

resource "digitalocean_spaces_key" "artifacts" {
  name = "${local.resource_name}-spaces-key"

  grant {
    bucket     = digitalocean_spaces_bucket.artifacts.name
    permission = "readwrite"
  }
}

resource "digitalocean_firewall" "cloud" {
  name        = "${local.resource_name}-fw"
  droplet_ids = [digitalocean_droplet.cloud.id]
  tags        = local.tags

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
