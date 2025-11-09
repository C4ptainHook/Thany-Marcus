param(
    [switch]$Force,
    [switch]$SkipConfirmation
)

Write-Host "============================================" -ForegroundColor Red
Write-Host "  COMPLETE INFRASTRUCTURE CLEANUP" -ForegroundColor Red
Write-Host "============================================" -ForegroundColor Red
Write-Host ""
Write-Host "This will destroy ALL infrastructure:" -ForegroundColor Yellow
Write-Host "  - Kubernetes applications (n8n, Redis, PostgreSQL)" -ForegroundColor Yellow
Write-Host "  - Monitoring stack (Prometheus, Grafana)" -ForegroundColor Yellow
Write-Host "  - Ingress controllers (nginx, haproxy)" -ForegroundColor Yellow
Write-Host "  - Kubernetes cluster" -ForegroundColor Yellow
Write-Host "  - VPC network" -ForegroundColor Yellow
Write-Host "  - All LoadBalancers and persistent volumes" -ForegroundColor Yellow
Write-Host ""

if (-not $SkipConfirmation) {
    $confirmation = Read-Host "Type 'DESTROY' to proceed"
    if ($confirmation -ne 'DESTROY') {
        Write-Host "Cleanup cancelled." -ForegroundColor Green
        exit 0
    }
}

Write-Host "`n[1/8] Destroying Kubernetes applications..." -ForegroundColor Cyan
cd D:\Marcus\infra\terraform\kubernetes-apps
if (Test-Path "terraform.tfstate") {
    terraform destroy -auto-approve
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Warning: Terraform destroy failed, continuing with manual cleanup..." -ForegroundColor Yellow
    }
} else {
    Write-Host "  No terraform state found, skipping..." -ForegroundColor Gray
}

Write-Host "`n[2/8] Uninstalling Helm releases..." -ForegroundColor Cyan
$helmReleases = @(
    @{Name="prometheus"; Namespace="monitoring"},
    @{Name="nginx-ingress"; Namespace="ingress-nginx"},
    @{Name="haproxy-ingress"; Namespace="ingress-haproxy"},
    @{Name="cert-manager"; Namespace="cert-manager"},
    @{Name="metrics-server"; Namespace="kube-system"}
)

foreach ($release in $helmReleases) {
    Write-Host "  Uninstalling $($release.Name) from $($release.Namespace)..." -ForegroundColor Gray
    helm uninstall $release.Name -n $release.Namespace 2>$null
}

Write-Host "`n[3/8] Deleting Kubernetes namespaces..." -ForegroundColor Cyan
$namespaces = @("backend", "monitoring", "ingress-nginx", "ingress-haproxy", "cert-manager")
foreach ($ns in $namespaces) {
    Write-Host "  Deleting namespace: $ns" -ForegroundColor Gray
    kubectl delete namespace $ns --ignore-not-found=true --timeout=60s 2>$null
}

Write-Host "`n[4/8] Force-deleting LoadBalancers..." -ForegroundColor Cyan
$lbs = doctl compute load-balancer list --format ID,Name --no-header 2>$null
$targetLbs = $lbs | Select-String -Pattern "nginx-lb|haproxy-lb"

if ($targetLbs) {
    foreach ($lbLine in $targetLbs) {
        $lbId = ($lbLine -split '\s+')[0]
        $lbName = ($lbLine -split '\s+')[1]
        Write-Host "  Force-deleting LoadBalancer: $lbName ($lbId)" -ForegroundColor Gray
        doctl compute load-balancer delete $lbId --force 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "    Deleted successfully" -ForegroundColor Green
        } else {
            Write-Host "    Failed to delete, will retry after cluster deletion" -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "  No LoadBalancers found" -ForegroundColor Green
}

Write-Host "`n[5/8] Deleting Kubernetes cluster..." -ForegroundColor Cyan
$clusters = doctl kubernetes cluster list --format ID,Name --no-header
$clusterLine = $clusters | Select-String "knowledge-hub-k8s"
if ($clusterLine) {
    $clusterId = ($clusterLine -split '\s+')[0]
    Write-Host "  Deleting cluster: $clusterId" -ForegroundColor Gray
    doctl kubernetes cluster delete $clusterId --force

    Write-Host "  Waiting for cluster deletion (this releases LoadBalancers)..." -ForegroundColor Gray
    Start-Sleep -Seconds 60

    Write-Host "  Retrying LoadBalancer deletion after cluster removal..." -ForegroundColor Gray
    $lbs = doctl compute load-balancer list --format ID,Name --no-header 2>$null
    $targetLbs = $lbs | Select-String -Pattern "nginx-lb|haproxy-lb"
    if ($targetLbs) {
        foreach ($lbLine in $targetLbs) {
            $lbId = ($lbLine -split '\s+')[0]
            $lbName = ($lbLine -split '\s+')[1]
            Write-Host "    Deleting: $lbName ($lbId)" -ForegroundColor Gray
            doctl compute load-balancer delete $lbId --force 2>$null
        }
    }
} else {
    Write-Host "  No cluster found, skipping..." -ForegroundColor Gray
}

Write-Host "`n[6/8] Deleting VPCs..." -ForegroundColor Cyan
$vpcs = doctl vpcs list --format ID,Name --no-header
$vpcLines = $vpcs | Select-String "vpc-"
foreach ($vpcLine in $vpcLines) {
    $vpcId = ($vpcLine -split '\s+')[0]
    $vpcName = ($vpcLine -split '\s+')[1]
    if ($vpcName -ne "default-fra1") {
        Write-Host "  Deleting VPC: $vpcName ($vpcId)" -ForegroundColor Gray
        doctl vpcs delete $vpcId --force 2>$null
    }
}

Write-Host "`n[7/8] Checking for orphaned resources..." -ForegroundColor Cyan

Write-Host "  Checking LoadBalancers..." -ForegroundColor Gray
$remainingLbs = doctl compute load-balancer list --format ID,Name,Status --no-header
if ($remainingLbs) {
    Write-Host "  WARNING: Orphaned LoadBalancers found:" -ForegroundColor Red
    doctl compute load-balancer list

    if ($Force) {
        Write-Host "  Force flag enabled, deleting orphaned LoadBalancers..." -ForegroundColor Yellow
        foreach ($lb in $remainingLbs) {
            $lbId = ($lb -split '\s+')[0]
            doctl compute load-balancer delete $lbId --force
        }
    }
} else {
    Write-Host "  No orphaned LoadBalancers." -ForegroundColor Green
}

Write-Host "  Checking volumes..." -ForegroundColor Gray
$remainingVolumes = doctl compute volume list --format ID,Name --no-header
if ($remainingVolumes) {
    Write-Host "  WARNING: Orphaned volumes found:" -ForegroundColor Red
    doctl compute volume list

    if ($Force) {
        Write-Host "  Force flag enabled, deleting orphaned volumes..." -ForegroundColor Yellow
        foreach ($vol in $remainingVolumes) {
            $volId = ($vol -split '\s+')[0]
            doctl compute volume delete $volId --force
        }
    }
} else {
    Write-Host "  No orphaned volumes." -ForegroundColor Green
}

Write-Host "`n[8/8] Cleaning up Terraform state files..." -ForegroundColor Cyan
if (Test-Path "D:\Marcus\infra\terraform\infrastructure\terraform.tfstate") {
    Remove-Item "D:\Marcus\infra\terraform\infrastructure\terraform.tfstate*" -Force
    Write-Host "  Removed infrastructure state files" -ForegroundColor Gray
}
if (Test-Path "D:\Marcus\infra\terraform\kubernetes-apps\terraform.tfstate") {
    Remove-Item "D:\Marcus\infra\terraform\kubernetes-apps\terraform.tfstate*" -Force
    Write-Host "  Removed kubernetes-apps state files" -ForegroundColor Gray
}

Write-Host "`n============================================" -ForegroundColor Green
Write-Host "  CLEANUP COMPLETE!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""
Write-Host "Summary:" -ForegroundColor Cyan
Write-Host "  - Kubernetes applications: Destroyed" -ForegroundColor Gray
Write-Host "  - Helm releases: Uninstalled" -ForegroundColor Gray
Write-Host "  - Namespaces: Deleted" -ForegroundColor Gray
Write-Host "  - Kubernetes cluster: Deleted" -ForegroundColor Gray
Write-Host "  - VPCs: Deleted" -ForegroundColor Gray
Write-Host "  - Terraform state: Cleaned" -ForegroundColor Gray
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Run: .\infra\scripts\setup-all.ps1" -ForegroundColor White
Write-Host "  2. Configure DNS A records after deployment" -ForegroundColor White
Write-Host "  3. Access Grafana at https://grafana.thany.click" -ForegroundColor White
