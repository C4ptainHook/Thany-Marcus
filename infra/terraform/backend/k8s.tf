resource "kubernetes_namespace" "backend" {
  count = var.enable_kubernetes ? 1 : 0
  metadata {
    name = "backend"
    labels = {
      name = "backend"
    }
  }
}

resource "kubernetes_manifest" "metrics_server" {
  count = var.enable_kubernetes ? 1 : 0
  manifest = yamldecode(file("${path.module}/k8s-manifests/metrics-server.yaml"))
  depends_on = [digitalocean_kubernetes_cluster.main]
}

resource "helm_release" "cert_manager" {
  count            = var.enable_kubernetes ? 1 : 0
  name             = "cert-manager"
  repository       = "https://charts.jetstack.io"
  chart            = "cert-manager"
  version          = "v1.13.2"
  namespace        = "cert-manager"
  create_namespace = true

  values = [
    yamlencode({
      installCRDs = true
    })
  ]

  depends_on = [digitalocean_kubernetes_cluster.main]
}

resource "kubernetes_manifest" "cluster_issuer_nginx" {
  count = var.enable_kubernetes ? 1 : 0

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

  depends_on = [helm_release.cert_manager]
}

resource "kubernetes_manifest" "cluster_issuer_haproxy" {
  count = var.enable_kubernetes ? 1 : 0

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

  depends_on = [helm_release.cert_manager]
}

resource "helm_release" "nginx_ingress" {
  count            = var.enable_kubernetes ? 1 : 0
  name             = "nginx-ingress"
  repository       = "https://kubernetes.github.io/ingress-nginx"
  chart            = "ingress-nginx"
  version          = "4.8.3"
  namespace        = "ingress-nginx"
  create_namespace = true

  values = [
    yamlencode({
      controller = {
        service = {
          annotations = {
            "service.beta.kubernetes.io/do-loadbalancer-name" = "nginx-lb"
          }
        }
        metrics = {
          enabled = true
          serviceMonitor = {
            enabled = true
          }
        }
      }
    })
  ]

  depends_on = [digitalocean_kubernetes_cluster.main]
}

resource "helm_release" "haproxy_ingress" {
  count            = var.enable_kubernetes ? 1 : 0
  name             = "haproxy-ingress"
  repository       = "https://haproxytech.github.io/helm-charts"
  chart            = "kubernetes-ingress"
  version          = "1.37.0"
  namespace        = "ingress-haproxy"
  create_namespace = true

  values = [
    yamlencode({
      controller = {
        service = {
          annotations = {
            "service.beta.kubernetes.io/do-loadbalancer-name" = "haproxy-lb"
          }
        }
        metrics = {
          enabled = true
        }
      }
    })
  ]

  depends_on = [digitalocean_kubernetes_cluster.main]
}

resource "kubernetes_manifest" "backend_deployment" {
  count = var.enable_kubernetes ? 1 : 0
  manifest = yamldecode(file("${path.module}/k8s-manifests/deployment.yaml"))

  depends_on = [kubernetes_namespace.backend]
}

resource "kubernetes_manifest" "backend_service" {
  count = var.enable_kubernetes ? 1 : 0
  manifest = yamldecode(file("${path.module}/k8s-manifests/service.yaml"))

  depends_on = [kubernetes_namespace.backend]
}

resource "kubernetes_manifest" "backend_hpa" {
  count = var.enable_kubernetes ? 1 : 0
  manifest = yamldecode(file("${path.module}/k8s-manifests/hpa.yaml"))

  depends_on = [
    kubernetes_manifest.backend_deployment,
    kubernetes_manifest.metrics_server
  ]
}

resource "kubernetes_manifest" "ingress_nginx" {
  count = var.enable_kubernetes ? 1 : 0
  manifest = yamldecode(file("${path.module}/k8s-manifests/ingress-nginx.yaml"))

  depends_on = [
    kubernetes_manifest.backend_service,
    helm_release.nginx_ingress,
    kubernetes_manifest.cluster_issuer_nginx
  ]
}

resource "kubernetes_manifest" "ingress_haproxy" {
  count = var.enable_kubernetes ? 1 : 0
  manifest = yamldecode(file("${path.module}/k8s-manifests/ingress-haproxy.yaml"))

  depends_on = [
    kubernetes_manifest.backend_service,
    helm_release.haproxy_ingress,
    kubernetes_manifest.cluster_issuer_haproxy
  ]
}
