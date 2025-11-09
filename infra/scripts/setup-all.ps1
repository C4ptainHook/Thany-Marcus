param(
    [Parameter(Mandatory=$false)]
    [string]$CertbotEmail,

    [Parameter(Mandatory=$false)]
    [string]$GrafanaPassword = "admin"
)

Write-Host "============================================" -ForegroundColor Green
Write-Host "  INFRASTRUCTURE SETUP" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""

if (-not $CertbotEmail) {
    Write-Host "ERROR: -CertbotEmail parameter is required" -ForegroundColor Red
    Write-Host "Usage: .\setup-all.ps1 -CertbotEmail your@email.com [-GrafanaPassword admin]" -ForegroundColor Yellow
    exit 1
}

$DO_TOKEN = $env:DO_TOKEN
if (-not $DO_TOKEN) {
    Write-Host "ERROR: DO_TOKEN environment variable not set" -ForegroundColor Red
    Write-Host "Set it with: `$env:DO_TOKEN='your-token'" -ForegroundColor Yellow
    exit 1
}

Write-Host "Configuration:" -ForegroundColor Cyan
Write-Host "  Certbot Email: $CertbotEmail" -ForegroundColor Gray
Write-Host "  Grafana Password: ****" -ForegroundColor Gray
Write-Host ""

Write-Host "[1/4] Deploying infrastructure (VPC + Cluster + Helm releases)..." -ForegroundColor Cyan
cd D:\Marcus\infra\terraform\infrastructure

if (-not (Test-Path "terraform.tfvars")) {
    Write-Host "  Creating terraform.tfvars..." -ForegroundColor Gray
    @"
do_token = "$DO_TOKEN"
certbot_email = "$CertbotEmail"
grafana_password = "$GrafanaPassword"
enable_kubernetes = true
kubernetes_node_count = 1
kubernetes_node_size = "s-4vcpu-8gb"
ssh_key_names = []
"@ | Out-File -FilePath "terraform.tfvars" -Encoding UTF8
}

Write-Host "  Running terraform init..." -ForegroundColor Gray
terraform init
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Terraform init failed" -ForegroundColor Red
    exit 1
}

Write-Host "  Running terraform apply..." -ForegroundColor Gray
terraform apply -auto-approve
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Terraform apply failed" -ForegroundColor Red
    exit 1
}

Write-Host "`n[2/4] Configuring kubectl..." -ForegroundColor Cyan
$kubeconfig = terraform output -raw kube_config
if ($kubeconfig) {
    $kubeconfig | Out-File -FilePath "$env:USERPROFILE\.kube\config" -Encoding UTF8
    Write-Host "  kubeconfig saved to ~/.kube/config" -ForegroundColor Green
} else {
    Write-Host "ERROR: Could not get kubeconfig from Terraform output" -ForegroundColor Red
    exit 1
}

Write-Host "  Verifying cluster connection..." -ForegroundColor Gray
kubectl cluster-info
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Cannot connect to cluster" -ForegroundColor Red
    exit 1
}

Write-Host "`n[3/4] Waiting for Helm releases to be ready..." -ForegroundColor Cyan

Write-Host "  Waiting for cert-manager..." -ForegroundColor Gray
kubectl wait --for=condition=ready pod -l app.kubernetes.io/instance=cert-manager -n cert-manager --timeout=300s

Write-Host "  Waiting for metrics-server..." -ForegroundColor Gray
kubectl wait --for=condition=ready pod -l app.kubernetes.io/name=metrics-server -n kube-system --timeout=300s

Write-Host "  Waiting for nginx-ingress..." -ForegroundColor Gray
kubectl wait --for=condition=ready pod -l app.kubernetes.io/name=ingress-nginx -n ingress-nginx --timeout=300s

Write-Host "  Waiting for haproxy-ingress..." -ForegroundColor Gray
kubectl wait --for=condition=ready pod -l app.kubernetes.io/name=kubernetes-ingress -n ingress-haproxy --timeout=300s

Write-Host "  Waiting for prometheus..." -ForegroundColor Gray
kubectl wait --for=condition=ready pod -l app.kubernetes.io/name=grafana -n monitoring --timeout=300s

Write-Host "`n[4/4] Deploying applications..." -ForegroundColor Cyan
cd D:\Marcus\infra\terraform\kubernetes-apps

Write-Host "  Creating terraform.tfvars from infrastructure outputs..." -ForegroundColor Gray
& .\create-tfvars.ps1

Write-Host "  Running terraform init..." -ForegroundColor Gray
terraform init
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Terraform init failed" -ForegroundColor Red
    exit 1
}

Write-Host "  Running terraform apply..." -ForegroundColor Gray
terraform apply -auto-approve
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Terraform apply failed" -ForegroundColor Red
    exit 1
}

Write-Host "`n============================================" -ForegroundColor Green
Write-Host "  DEPLOYMENT COMPLETE!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""

Write-Host "Getting LoadBalancer IPs..." -ForegroundColor Cyan
cd D:\Marcus\infra\terraform\infrastructure
$nginxIp = terraform output -raw nginx_lb_ip
$haproxyIp = terraform output -raw haproxy_lb_ip

Write-Host ""
Write-Host "DNS Configuration Required:" -ForegroundColor Yellow
Write-Host "  Create these A records in your DNS provider:" -ForegroundColor White
Write-Host "    nginx.thany.click    -> $nginxIp" -ForegroundColor Cyan
Write-Host "    haproxy.thany.click  -> $haproxyIp" -ForegroundColor Cyan
Write-Host "    grafana.thany.click  -> $nginxIp" -ForegroundColor Cyan
Write-Host ""

Write-Host "Access Points:" -ForegroundColor Yellow
Write-Host "  n8n (nginx):    https://nginx.thany.click" -ForegroundColor White
Write-Host "  n8n (haproxy):  https://haproxy.thany.click" -ForegroundColor White
Write-Host "  Grafana:        https://grafana.thany.click" -ForegroundColor White
Write-Host "    Username: admin" -ForegroundColor Gray
Write-Host "    Password: $GrafanaPassword" -ForegroundColor Gray
Write-Host ""

Write-Host "Verify Deployment:" -ForegroundColor Yellow
Write-Host "  kubectl get pods -A" -ForegroundColor White
Write-Host "  kubectl get hpa -n backend" -ForegroundColor White
Write-Host "  kubectl get ingress -n backend" -ForegroundColor White
Write-Host ""

Write-Host "Load Testing:" -ForegroundColor Yellow
Write-Host "  k6 run D:\Marcus\infra\scripts\stress-test.js" -ForegroundColor White
Write-Host ""

Write-Host "Cleanup:" -ForegroundColor Yellow
Write-Host "  .\infra\scripts\cleanup-all.ps1" -ForegroundColor White
