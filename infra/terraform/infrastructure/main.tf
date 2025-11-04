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

resource "digitalocean_vpc" "main" {
  name   = "vpc-${random_id.suffix.hex}"
  region = var.region
}

resource "digitalocean_kubernetes_cluster" "main" {
  count    = var.enable_kubernetes ? 1 : 0
  name     = "${var.name_prefix}-k8s-${random_id.suffix.hex}"
  region   = var.region
  version  = var.kubernetes_version
  vpc_uuid = digitalocean_vpc.main.id

  node_pool {
    name       = "worker-pool"
    size       = var.kubernetes_node_size
    node_count = var.kubernetes_node_count
    auto_scale = var.kubernetes_autoscale
    min_nodes  = var.kubernetes_autoscale ? var.kubernetes_min_nodes : null
    max_nodes  = var.kubernetes_autoscale ? var.kubernetes_max_nodes : null
    tags       = concat(var.tags, ["k8s-worker"])
  }

  tags = concat(var.tags, ["kubernetes"])

  lifecycle {
    ignore_changes = [node_pool[0].node_count]
  }
}

