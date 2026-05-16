Write-Host "Fetching infrastructure outputs..." -ForegroundColor Cyan

$infraPath = "..\infrastructure"

Push-Location $infraPath
$cluster_endpoint = terraform output -raw kubernetes_endpoint
$cluster_token = terraform output -raw cluster_token
$cluster_ca_certificate = terraform output -raw cluster_ca_certificate
$certbot_email = terraform output -raw certbot_email
Pop-Location

$tfvars = @"
cluster_endpoint       = "$cluster_endpoint"
cluster_token          = "$cluster_token"
cluster_ca_certificate = "$cluster_ca_certificate"
certbot_email          = "$certbot_email"
"@

$tfvars | Out-File -FilePath "terraform.tfvars" -Encoding UTF8

Write-Host "✓ Created terraform.tfvars" -ForegroundColor Green
Write-Host "You can now run: terraform apply" -ForegroundColor Yellow
