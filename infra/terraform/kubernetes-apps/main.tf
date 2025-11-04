resource "random_password" "postgres_password" {
  length  = 32
  special = true
}

resource "random_password" "n8n_encryption_key" {
  length  = 32
  special = false
}

resource "kubernetes_secret" "postgres_secret" {
  metadata {
    name      = "postgres-secret"
    namespace = "backend"
  }

  data = {
    POSTGRES_DB       = "n8n"
    POSTGRES_USER     = "n8n"
    POSTGRES_PASSWORD = random_password.postgres_password.result
  }
}

resource "kubernetes_secret" "n8n_secret" {
  metadata {
    name      = "n8n-secret"
    namespace = "backend"
  }

  data = {
    N8N_ENCRYPTION_KEY = random_password.n8n_encryption_key.result
  }
}

resource "kubernetes_manifest" "cluster_issuer_nginx" {
  manifest = {
    apiVersion = "cert-manager.io/v1"
    kind       = "ClusterIssuer"
    metadata = {
      name = "letsencrypt-nginx"
    }
    spec = {
      acme = {
        server = "https://acme-v02.api.letsencrypt.org/directory"
        email  = var.certbot_email
        privateKeySecretRef = {
          name = "letsencrypt-nginx-key"
        }
        solvers = [{
          http01 = {
            ingress = {
              class = "nginx"
            }
          }
        }]
      }
    }
  }
}

resource "kubernetes_manifest" "cluster_issuer_haproxy" {
  manifest = {
    apiVersion = "cert-manager.io/v1"
    kind       = "ClusterIssuer"
    metadata = {
      name = "letsencrypt-haproxy"
    }
    spec = {
      acme = {
        server = "https://acme-v02.api.letsencrypt.org/directory"
        email  = var.certbot_email
        privateKeySecretRef = {
          name = "letsencrypt-haproxy-key"
        }
        solvers = [{
          http01 = {
            ingress = {
              class = "haproxy"
            }
          }
        }]
      }
    }
  }
}

locals {
  redis_manifests      = [for doc in split("---", file("${path.module}/k8s-manifests/redis.yaml")) : yamldecode(doc) if trimspace(doc) != ""]
  postgres_manifests   = [for doc in split("---", file("${path.module}/k8s-manifests/postgres.yaml")) : yamldecode(doc) if trimspace(doc) != ""]
  n8n_editor_manifests = [for doc in split("---", file("${path.module}/k8s-manifests/n8n-editor.yaml")) : yamldecode(doc) if trimspace(doc) != ""]
  n8n_worker_manifests = [for doc in split("---", file("${path.module}/k8s-manifests/n8n-worker.yaml")) : yamldecode(doc) if trimspace(doc) != ""]
}

resource "kubernetes_manifest" "redis_service" {
  manifest = local.redis_manifests[0]
}

resource "kubernetes_manifest" "redis_deployment" {
  manifest = local.redis_manifests[1]

  depends_on = [kubernetes_manifest.redis_service]
}

resource "kubernetes_manifest" "postgres_service" {
  manifest = local.postgres_manifests[0]

  depends_on = [kubernetes_secret.postgres_secret]
}

resource "kubernetes_manifest" "postgres_pvc" {
  manifest = local.postgres_manifests[1]
}

resource "kubernetes_manifest" "postgres_deployment" {
  manifest = local.postgres_manifests[2]

  depends_on = [
    kubernetes_manifest.postgres_pvc,
    kubernetes_secret.postgres_secret
  ]
}

resource "kubernetes_manifest" "n8n_editor_service" {
  manifest = local.n8n_editor_manifests[0]

  depends_on = [
    kubernetes_manifest.redis_deployment,
    kubernetes_manifest.postgres_deployment
  ]
}

resource "kubernetes_manifest" "n8n_editor_deployment" {
  manifest = local.n8n_editor_manifests[1]

  depends_on = [
    kubernetes_manifest.n8n_editor_service,
    kubernetes_secret.postgres_secret
  ]
}

resource "kubernetes_manifest" "n8n_worker_deployment" {
  manifest = local.n8n_worker_manifests[0]

  depends_on = [
    kubernetes_manifest.redis_deployment,
    kubernetes_manifest.postgres_deployment,
    kubernetes_secret.postgres_secret
  ]
}

resource "kubernetes_manifest" "n8n_worker_hpa" {
  manifest = local.n8n_worker_manifests[1]

  depends_on = [kubernetes_manifest.n8n_worker_deployment]
}

resource "kubernetes_manifest" "ingress_nginx" {
  manifest = yamldecode(file("${path.module}/k8s-manifests/ingress-nginx.yaml"))

  depends_on = [
    kubernetes_manifest.n8n_editor_service,
    kubernetes_manifest.cluster_issuer_nginx
  ]
}

resource "kubernetes_manifest" "ingress_haproxy" {
  manifest = yamldecode(file("${path.module}/k8s-manifests/ingress-haproxy.yaml"))

  depends_on = [
    kubernetes_manifest.n8n_editor_service,
    kubernetes_manifest.cluster_issuer_haproxy
  ]
}

resource "kubernetes_manifest" "ingress_grafana" {
  manifest = yamldecode(file("${path.module}/k8s-manifests/ingress-grafana.yaml"))

  depends_on = [
    kubernetes_manifest.cluster_issuer_nginx
  ]
}
