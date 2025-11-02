output "droplet_ip" {
  value = digitalocean_droplet.n8n_vm.ipv4_address
}

output "ssh_connect" {
  value = "ssh ${var.linux_admin_user}@${digitalocean_droplet.n8n_vm.ipv4_address}"
}

output "droplet_id" {
  value = digitalocean_droplet.n8n_vm.id
}

output "droplet_ipv6" {
  value = digitalocean_droplet.n8n_vm.ipv6_address
}

output "floating_ip_address" {
  description = "The permanent public IP address for the n8n service."
  value       = digitalocean_floating_ip.n8n_ip.ip_address
}

output "kubernetes_cluster_id" {
  value = try(digitalocean_kubernetes_cluster.main[0].id, "")
}

output "kubernetes_endpoint" {
  value = try(digitalocean_kubernetes_cluster.main[0].endpoint, "")
}

output "kubernetes_cluster_name" {
  value = try(digitalocean_kubernetes_cluster.main[0].name, "")
}

output "kubeconfig_command" {
  value = var.enable_kubernetes ? "doctl kubernetes cluster kubeconfig save ${digitalocean_kubernetes_cluster.main[0].id}" : ""
}

output "vpc_id" {
  description = "VPC ID for use by monitoring infrastructure"
  value       = digitalocean_vpc.main.id
}

output "nginx_lb_ip" {
  description = "nginx LoadBalancer IP (use for DNS A record: nginx.thany.click)"
  value       = var.enable_kubernetes ? "Check: kubectl get svc -n ingress-nginx nginx-ingress-ingress-nginx-controller -o jsonpath='{.status.loadBalancer.ingress[0].ip}'" : ""
}

output "haproxy_lb_ip" {
  description = "haproxy LoadBalancer IP (use for DNS A record: haproxy.thany.click)"
  value       = var.enable_kubernetes ? "Check: kubectl get svc -n ingress-haproxy haproxy-ingress-kubernetes-ingress -o jsonpath='{.status.loadBalancer.ingress[0].ip}'" : ""
}

