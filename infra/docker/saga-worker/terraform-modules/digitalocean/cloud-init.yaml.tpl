#cloud-config
bootcmd:
  - mkdir -p /opt/thany-cloud /etc/thany-cloud /var/log/thany-cloud /run/cloud-secrets
  - chmod 0700 /run/cloud-secrets
  - timedatectl set-timezone ${timezone}

users:
  - default
  - name: ${admin_user}
    groups: [adm, sudo]
    shell: /bin/bash
    lock_passwd: true
    sudo: "ALL=(ALL) NOPASSWD:ALL"
    ssh_authorized_keys:
      - ${ssh_public_key}

ssh_pwauth: false

package_update: true
package_upgrade: false
packages:
  - unattended-upgrades
  - ufw
  - fail2ban
  - curl
  - gnupg
  - jq
  - gettext-base
  - ca-certificates
  - nginx
  - certbot
  - python3-certbot-nginx

write_files:
  - path: /etc/ssh/sshd_config.d/01-hardening.conf
    owner: root:root
    permissions: "0644"
    content: |
      PasswordAuthentication no
      PermitRootLogin no
      ChallengeResponseAuthentication no
      UsePAM yes
      X11Forwarding no
      ClientAliveInterval 300
      ClientAliveCountMax 2
      AllowUsers ${admin_user}

  - path: /etc/thany-cloud/cloud.env.tpl
    owner: root:root
    permissions: "0640"
    content: |
      CLOUD_ID=${cloud_id}
      DOMAIN=${hostname}
      LE_EMAIL=${le_email}
      LE_ACME_CA=${le_acme_ca}
      IMAGE_TAG=${image_tag}
      ENROLLMENT_TOKEN=${enrollment_token}
      PORTAL_CALLBACK_URL=${portal_callback_url}
      CLOUD_ADMIN_TOKEN=$${CLOUD_ADMIN_TOKEN}
      JWT_SIGNING_KEY=$${JWT_SIGNING_KEY}
      POSTGRES_PASSWORD=$${POSTGRES_PASSWORD}

  - path: /etc/thany-cloud/nginx-site.tpl
    owner: root:root
    permissions: "0644"
    content: |
      server {
          listen 80;
          listen [::]:80;
          server_name $${DOMAIN};

          location /internal/ {
              return 404;
          }

          location / {
              proxy_pass http://127.0.0.1:8080;
              proxy_set_header Host $$host;
              proxy_set_header X-Real-IP $$remote_addr;
              proxy_set_header X-Forwarded-For $$proxy_add_x_forwarded_for;
              proxy_set_header X-Forwarded-Proto $$scheme;
              proxy_http_version 1.1;
              proxy_set_header Upgrade $$http_upgrade;
              proxy_set_header Connection "upgrade";
          }
      }

  - path: /etc/systemd/system/thany-cloud.service
    owner: root:root
    permissions: "0644"
    content: |
      [Unit]
      Description=Thany Cloud Docker Compose
      Requires=docker.service
      After=docker.service network-online.target
      Wants=network-online.target

      [Service]
      Type=oneshot
      RemainAfterExit=yes
      WorkingDirectory=/opt/thany-cloud
      User=${admin_user}
      ExecStart=/usr/bin/docker compose up -d
      ExecStop=/usr/bin/docker compose down

      [Install]
      WantedBy=multi-user.target

  - path: /etc/ufw/applications.d/thany-cloud
    owner: root:root
    permissions: "0644"
    content: |
      [thany-cloud-web]
      title=Thany Cloud (HTTP/HTTPS)
      description=nginx + LE
      ports=80,443/tcp

  - path: /etc/apt/apt.conf.d/50unattended-upgrades
    owner: root:root
    permissions: "0644"
    content: |
      Unattended-Upgrade::Allowed-Origins {
        "$${distro_id}:$${distro_codename}";
        "$${distro_id}:$${distro_codename}-security";
        "$${distro_id}:$${distro_codename}-updates";
      };
      Unattended-Upgrade::Automatic-Reboot "true";
      Unattended-Upgrade::Automatic-Reboot-Time "03:30";

  - path: /opt/thany-cloud/docker-compose.yml
    owner: root:root
    permissions: "0644"
    content: |
      services:
        cloud-api:
          image: ghcr.io/c4ptainhook/thany-cloud-api:$${IMAGE_TAG:-latest}
          restart: unless-stopped
          ports:
            - "127.0.0.1:8080:8080"
          environment:
            ASPNETCORE_ENVIRONMENT: Production
            ASPNETCORE_URLS: "http://+:8080"
            ConnectionStrings__Cloud: Host=postgres;Port=5432;Username=cloud;Password=$${POSTGRES_PASSWORD};Database=cloud
            Bootstrap__CloudId: $${CLOUD_ID}
            Bootstrap__Hostname: $${DOMAIN}
            Bootstrap__EnrollmentToken: $${ENROLLMENT_TOKEN}
            Bootstrap__CloudAdminToken: $${CLOUD_ADMIN_TOKEN}
            Bootstrap__PortalCallbackUrl: $${PORTAL_CALLBACK_URL}
            Cert__LiveDir: /etc/letsencrypt/live/$${DOMAIN}
          volumes:
            - /etc/letsencrypt:/etc/letsencrypt:ro
          depends_on:
            postgres:
              condition: service_healthy
          networks: [cloud]
          healthcheck:
            test: ["CMD-SHELL", "wget -q -O /dev/null http://localhost:8080/health/live || exit 1"]
            interval: 10s
            timeout: 5s
            retries: 5

        postgres:
          image: postgres:16-alpine
          restart: unless-stopped
          environment:
            POSTGRES_USER: cloud
            POSTGRES_PASSWORD: $${POSTGRES_PASSWORD}
            POSTGRES_DB: cloud
          volumes:
            - pg-data:/var/lib/postgresql/data
          networks: [cloud]
          healthcheck:
            test: ["CMD-SHELL", "pg_isready -U cloud -d cloud"]
            interval: 5s
            timeout: 5s
            retries: 10

      volumes:
        pg-data:

      networks:
        cloud:

runcmd:
  - ufw default deny incoming
  - ufw default allow outgoing
  - ufw limit OpenSSH
  - ufw allow thany-cloud-web
  - ufw --force enable

  - install -m 0755 -d /etc/apt/keyrings
  - curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
  - chmod a+r /etc/apt/keyrings/docker.gpg
  - |
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo $VERSION_CODENAME) stable" \
      > /etc/apt/sources.list.d/docker.list
  - apt-get update
  - apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin
  - usermod -aG docker ${admin_user}

  - openssl rand -hex 32 > /run/cloud-secrets/cloud_admin_token
  - openssl rand -hex 32 > /run/cloud-secrets/jwt_signing_key
  - openssl rand -hex 32 > /run/cloud-secrets/postgres_password
  - chmod 0600 /run/cloud-secrets/cloud_admin_token /run/cloud-secrets/jwt_signing_key /run/cloud-secrets/postgres_password

  - |
    set -a
    CLOUD_ADMIN_TOKEN=$(cat /run/cloud-secrets/cloud_admin_token)
    JWT_SIGNING_KEY=$(cat /run/cloud-secrets/jwt_signing_key)
    POSTGRES_PASSWORD=$(cat /run/cloud-secrets/postgres_password)
    set +a
    envsubst < /etc/thany-cloud/cloud.env.tpl > /opt/thany-cloud/.env
    chown ${admin_user}:${admin_user} /opt/thany-cloud/.env
    chmod 0640 /opt/thany-cloud/.env

  - chown ${admin_user}:${admin_user} /opt/thany-cloud/docker-compose.yml

  - |
    set -a
    # shellcheck disable=SC1091
    . /opt/thany-cloud/.env
    set +a
    envsubst '$DOMAIN' < /etc/thany-cloud/nginx-site.tpl > /etc/nginx/sites-available/$DOMAIN
    ln -sf /etc/nginx/sites-available/$DOMAIN /etc/nginx/sites-enabled/$DOMAIN
    rm -f /etc/nginx/sites-enabled/default
    nginx -t
    systemctl restart nginx

  - systemctl daemon-reload
  - systemctl enable --now thany-cloud.service

  - |
    set -a
    . /opt/thany-cloud/.env
    set +a
    for i in $(seq 1 60); do
      if curl -fsS -o /dev/null http://127.0.0.1:8080/health/live; then break; fi
      sleep 2
    done
    DEPLOY_HOOK="curl -fsS -X POST http://127.0.0.1:8080/internal/cert-installed -H 'Content-Type: application/json' -d '{\"event\":\"cert_installed\",\"identifier\":\"'$DOMAIN'\"}'"
    CERTBOT_FLAGS=""
    if [ "$LE_ACME_CA" = "https://acme-staging-v02.api.letsencrypt.org/directory" ]; then
      CERTBOT_FLAGS="--staging"
    fi
    certbot --nginx \
      -d "$DOMAIN" \
      --non-interactive \
      --agree-tos \
      -m "$LE_EMAIL" \
      --redirect \
      $CERTBOT_FLAGS \
      --deploy-hook "$DEPLOY_HOOK"

  - shred -u /run/cloud-secrets/cloud_admin_token /run/cloud-secrets/jwt_signing_key /run/cloud-secrets/postgres_password || true

  - systemctl restart ssh
  - systemctl enable --now unattended-upgrades
  - systemctl restart fail2ban || true

final_message: "Cloud-init completed in $UPTIME seconds. nginx + Cloud.Api running; registration fires on cert install."
