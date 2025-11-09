# Infrastructure Setup & Cleanup Guide

## Quick Start

### One-Command Setup
```powershell
.\infra\scripts\setup-all.ps1 -CertbotEmail your@email.com -GrafanaPassword yourpassword
```

### One-Command Cleanup
```powershell
.\infra\scripts\cleanup-all.ps1
```

---

## Prerequisites

1. **DigitalOcean Account & API Token**
   ```powershell
   $env:DO_TOKEN = "your-digitalocean-api-token"
   ```

2. **Required Tools**
   - Terraform
   - kubectl
   - doctl 
   - Helm

3. **DNS Domain**
   - You need a domain (e.g., thany.click) with DNS access

---

## Full Setup Process

### Step 1: Clean Up Existing Infrastructure (if any)
```powershell
cd D:\Marcus
.\infra\scripts\cleanup-all.ps1
```

This will:
- Destroy Kubernetes applications
- Uninstall all Helm releases
- Delete Kubernetes cluster
- Delete VPCs
- Clean up orphaned LoadBalancers and volumes
- Remove Terraform state files

### Step 2: Deploy New Infrastructure
```powershell
.\infra\scripts\setup-all.ps1 -CertbotEmail your@email.com -GrafanaPassword admin123
```

This will:
1. Deploy VPC + Kubernetes cluster
2. Install Helm releases (cert-manager, nginx, haproxy, prometheus, metrics-server)
3. Deploy applications (n8n-editor, n8n-worker, Redis, PostgreSQL)
4. Configure HPA for horizontal scaling
5. Create ingress resources with SSL

**Duration**: ~10-15 minutes

### Step 3: Configure DNS
After deployment, you'll get LoadBalancer IPs. Create these A records:

```
nginx.thany.click    -> <nginx-lb-ip>
haproxy.thany.click  -> <haproxy-lb-ip>
grafana.thany.click  -> <nginx-lb-ip>
```

### Step 4: Verify Deployment
```powershell
# Check all pods are running
kubectl get pods -A

# Check HPA configuration
kubectl get hpa -n backend

# Check ingress resources
kubectl get ingress -A

# Check SSL certificates
kubectl get certificate -A
```

### Step 5: Access Applications
- **n8n (nginx)**: https://nginx.thany.click
- **n8n (haproxy)**: https://haproxy.thany.click
- **Grafana**: https://grafana.thany.click (admin / yourpassword)

---

## Load Testing

### Quick Test
```powershell
k6 run D:\Marcus\infra\scripts\load-test-nginx.js
```

### Stress Test (triggers HPA scaling)
```powershell
k6 run D:\Marcus\infra\scripts\stress-test.js
```

### Monitor Scaling
```powershell
# Terminal 1: Watch HPA
kubectl get hpa -n backend -w

# Terminal 2: Watch pods
kubectl get pods -n backend -w

# Terminal 3: Watch metrics
kubectl top pods -n backend
```

---

## Manual Operations

### Deploy Only Infrastructure
```powershell
cd D:\Marcus\infra\terraform\infrastructure
terraform init
terraform apply
```

### Deploy Only Applications
```powershell
cd D:\Marcus\infra\terraform\kubernetes-apps
.\create-tfvars.ps1  # Generate tfvars from infrastructure outputs
terraform init
terraform apply
```

### Destroy Only Applications
```powershell
cd D:\Marcus\infra\terraform\kubernetes-apps
terraform destroy
```

### Destroy Only Infrastructure
```powershell
cd D:\Marcus\infra\terraform\infrastructure
terraform destroy
```

**WARNING**: Manual destroy may leave orphaned LoadBalancers. Always use `cleanup-all.ps1` for complete cleanup.

---

## Troubleshooting

### Issue: Terraform Helm provider errors
**Error**: `cannot re-use a name that is still in use`

**Solution**: Resources already exist. Run cleanup script first:
```powershell
.\infra\scripts\cleanup-all.ps1
```

### Issue: LoadBalancers not deleted after destroy
**Solution**: Delete manually:
```powershell
doctl compute load-balancer list
doctl compute load-balancer delete <lb-id> --force
```

### Issue: SSL certificate stuck in "Pending"
**Cause**: DNS not propagated or Let's Encrypt rate limit

**Solution**:
1. Verify DNS: `nslookup grafana.thany.click`
2. Check cert-manager logs: `kubectl logs -n cert-manager deployment/cert-manager`
3. Delete and recreate certificate: `kubectl delete certificate -n monitoring grafana-tls-cert`

### Issue: HPA not scaling
**Cause**: No workload triggering CPU usage

**Solution**: Create workflow in n8n with webhook trigger, then load test the webhook URL

## Architecture Overview

```
Internet
  ├─ nginx.thany.click → nginx LoadBalancer → nginx ingress → n8n-editor
  ├─ haproxy.thany.click → haproxy LoadBalancer → haproxy ingress → n8n-editor
  └─ grafana.thany.click → nginx LoadBalancer → nginx ingress → Grafana

Kubernetes Cluster
  ├─ n8n-editor (1 replica, stateless)
  ├─ n8n-worker (1-10 replicas, HPA, CPU 50%)
  ├─ Redis (queue)
  ├─ PostgreSQL (database)
  ├─ Prometheus (metrics)
  └─ Grafana (visualization)
```