resource "kubernetes_namespace" "backend" {
  count = var.enable_kubernetes ? 1 : 0
  metadata {
    name = "backend"
    labels = {
      name = "backend"
    }
  }
}

resource "helm_release" "metrics_server" {
  count      = var.enable_kubernetes ? 1 : 0
  name       = "metrics-server"
  repository = "https://kubernetes-sigs.github.io/metrics-server/"
  chart      = "metrics-server"
  version    = "3.12.0"
  namespace  = "kube-system"

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
            enabled = false
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
          type = "LoadBalancer"
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

resource "helm_release" "prometheus_stack" {
  count            = var.enable_kubernetes ? 1 : 0
  name             = "prometheus"
  repository       = "https://prometheus-community.github.io/helm-charts"
  chart            = "kube-prometheus-stack"
  version          = "65.1.1"
  namespace        = "monitoring"
  create_namespace = true

  values = [
    yamlencode({
      grafana = {
        service = {
          type = "ClusterIP"
        }
        adminPassword = var.grafana_password
        persistence = {
          enabled = false
        }
      }
      prometheus = {
        prometheusSpec = {
          serviceMonitorSelectorNilUsesHelmValues = false
          podMonitorSelectorNilUsesHelmValues     = false
          retention                               = "7d"
          storageSpec = {
            volumeClaimTemplate = {
              spec = {
                accessModes = ["ReadWriteOnce"]
                resources = {
                  requests = {
                    storage = "10Gi"
                  }
                }
              }
            }
          }
        }
      }
      alertmanager = {
        enabled = false
      }
    })
  ]

  depends_on = [
    digitalocean_kubernetes_cluster.main,
    helm_release.metrics_server
  ]
}
