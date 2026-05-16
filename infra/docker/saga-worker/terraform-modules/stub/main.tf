variable "cloud_id" { type = string }
variable "region"   { type = string }
variable "size"     { type = string }
variable "hostname" { type = string }

resource "null_resource" "ping" {
  triggers = {
    cloud_id = var.cloud_id
    hostname = var.hostname
    region   = var.region
    size     = var.size
  }

  provisioner "local-exec" {
    command = "echo provisioned cloud_id=${var.cloud_id} hostname=${var.hostname}"
  }
}
