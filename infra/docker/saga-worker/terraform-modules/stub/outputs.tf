output "ip" {
  value      = "203.0.113.1"
  depends_on = [null_resource.ping]
}
