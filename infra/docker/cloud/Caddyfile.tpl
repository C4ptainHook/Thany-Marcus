{
  email ${LE_EMAIL}
  # ACME_CA_PLACEHOLDER
  admin caddy:2019
  events {
    on cert_obtained exec curl -fsS -X POST \
      http://cloud-api:8080/internal/caddy-events \
      -H "Content-Type: application/json" \
      -d '{"event":"cert_obtained","identifier":"{event.data.identifier}"}'
  }
}

${DOMAIN} {
  encode gzip
  handle /admin/* {
    reverse_proxy cloud-api:8080
  }
  handle /api/* {
    reverse_proxy cloud-api:8080
  }
  handle {
    respond 404
  }
}
