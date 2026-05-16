{
  email ${LE_EMAIL}
  # ACME_CA_PLACEHOLDER
}

${DOMAIN} {
  encode gzip
  reverse_proxy /admin/* cloud-api:8080
  reverse_proxy /api/*   cloud-api:8080
  respond 404
}
