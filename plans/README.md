# Kubernetes Applications Deployment

This module deploys applications and Kubernetes resources to an existing cluster created by the `01-infrastructure` module.

## Prerequisites

1. Complete deployment of `01-infrastructure` module
2. Kubernetes cluster must be running and accessible
3. cert-manager, metrics-server, and ingress controllers must be installed

## Deployment Steps

### 1. Generate terraform.tfvars

Run the helper script to automatically populate `terraform.tfvars` from the infrastructure module outputs:

```powershell
.\create-tfvars.ps1
```

This script fetches the following from `01-infrastructure`:
- `cluster_endpoint` - Kubernetes API server endpoint
- `cluster_token` - Authentication token
- `cluster_ca_certificate` - Cluster CA certificate
- `certbot_email` - Email for Let's Encrypt notifications

### 2. Initialize Terraform

```powershell
terraform init
```

### 3. Review the Plan

```powershell
terraform plan
```

Expected resources to be created:
- 2 ClusterIssuer resources (letsencrypt-nginx, letsencrypt-haproxy)
- 1 Deployment (backend application)
- 1 Service (api-service)
- 1 HorizontalPodAutoscaler (2-10 replicas, 50% CPU threshold)
- 2 Ingress resources (nginx.thany.click, haproxy.thany.click)

### 4. Apply Configuration

```powershell
terraform apply
```

### 5. Verify Deployment

Check that all resources are created:

```powershell
# Get cluster credentials
cd ..\01-infrastructure
terraform output -raw kube_config > ~\.kube\config

# Verify pods are running
kubectl get pods -n backend

# Check HPA status
kubectl get hpa -n backend

# Verify SSL certificates
kubectl get certificates -n backend

# Check ingress resources
kubectl get ingress -n backend
```

## DNS Configuration

After deployment, configure DNS A records:

1. Get LoadBalancer IPs from infrastructure module:
   ```powershell
   cd ..\01-infrastructure
   terraform output
   ```

2. Create A records:
   - `nginx.thany.click` → nginx LoadBalancer IP
   - `haproxy.thany.click` → haproxy LoadBalancer IP

## Resources Created

### ClusterIssuers
- **letsencrypt-nginx**: Issues SSL certificates for nginx ingress
- **letsencrypt-haproxy**: Issues SSL certificates for haproxy ingress

### Application Resources
- **Deployment**: Backend application with resource limits and health checks
- **Service**: ClusterIP service exposing port 80
- **HPA**: Auto-scales pods based on CPU utilization (50% threshold)

### Ingress Resources
- **api-nginx**: Ingress for nginx.thany.click with rate limiting (30 rps, 10 connections)
- **api-haproxy**: Ingress for haproxy.thany.click with rate limiting (30 rps)

## SSL Certificates

SSL certificates are automatically provisioned by cert-manager using Let's Encrypt:
- **HTTP-01 challenge**: Domain validation through ingress controllers
- **Automatic renewal**: Certificates renew 30 days before expiration
- **Certificate storage**: Stored as Kubernetes secrets (`nginx-tls-cert`, `haproxy-tls-cert`)

## Troubleshooting

### SSL Certificate Issues

Check certificate status:
```powershell
kubectl describe certificate nginx-tls-cert -n backend
kubectl describe certificate haproxy-tls-cert -n backend
```

View cert-manager logs:
```powershell
kubectl logs -n cert-manager deployment/cert-manager
```

### HPA Not Scaling

Verify metrics-server is running:
```powershell
kubectl get deployment metrics-server -n kube-system
```

Check HPA status:
```powershell
kubectl describe hpa api-hpa -n backend
```

### Ingress Issues

Check ingress controller logs:
```powershell
# nginx
kubectl logs -n ingress-nginx deployment/nginx-ingress-controller

# haproxy
kubectl logs -n ingress-haproxy deployment/haproxy-ingress-controller
```

## Cleanup

To remove all application resources:

```powershell
terraform destroy
```

**Note**: This only removes application resources. To destroy the entire infrastructure, run `terraform destroy` in the `01-infrastructure` module afterward.
