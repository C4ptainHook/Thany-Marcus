terraform {
  required_version = ">= 1.5.0"
  required_providers {
    digitalocean = {
      source  = "digitalocean/digitalocean"
      version = ">= 2.37.2"
    }
    random = {
      source  = "hashicorp/random"
      version = ">= 3.5.1"
    }
    kubernetes = {
      source  = "hashicorp/kubernetes"
      version = ">= 2.23.0"
    }
    helm = {
      source  = "hashicorp/helm"
      version = ">= 2.11.0"
    }
  }
}

provider "digitalocean" {
  token = var.do_token
}

provider "kubernetes" {
  host                   = try(digitalocean_kubernetes_cluster.main[0].endpoint, "")
  token                  = try(digitalocean_kubernetes_cluster.main[0].kube_config[0].token, "")
  cluster_ca_certificate = try(base64decode(digitalocean_kubernetes_cluster.main[0].kube_config[0].cluster_ca_certificate), "")
}

provider "helm" {
  kubernetes = {
    host                   = try(digitalocean_kubernetes_cluster.main[0].endpoint, "")
    token                  = try(digitalocean_kubernetes_cluster.main[0].kube_config[0].token, "")
    cluster_ca_certificate = try(base64decode(digitalocean_kubernetes_cluster.main[0].kube_config[0].cluster_ca_certificate), "")
  }
}

