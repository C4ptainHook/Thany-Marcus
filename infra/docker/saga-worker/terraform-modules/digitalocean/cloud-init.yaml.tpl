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
package_upgrade: true
packages:
  - unattended-upgrades
  - ufw
  - fail2ban
  - curl
  - gnupg
  - jq
  - gettext-base
  - ca-certificates

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

  - path: /etc/thany-cloud/Caddyfile.tpl
    owner: root:root
    permissions: "0644"
    content: |
      {
        email $${LE_EMAIL}
        # ACME_CA_PLACEHOLDER
      }
      $${DOMAIN} {
        encode gzip
        reverse_proxy /admin/* cloud-api:8080
        reverse_proxy /api/*   cloud-api:8080
        respond 404
      }

  - path: /usr/local/bin/register-with-portal.sh
    owner: root:root
    permissions: "0755"
    content: |
      #!/usr/bin/env bash
      set -euo pipefail
      ENV_FILE=/opt/thany-cloud/.env
      # shellcheck disable=SC1090
      source "$ENV_FILE"

      DEADLINE=$(( $(date +%s) + 1800 ))
      while [[ $(date +%s) -lt $DEADLINE ]]; do
        if curl -fsS "http://127.0.0.1/admin/health" -H "Host: $DOMAIN" \
             | jq -e '.cert_ready == true' > /dev/null 2>&1; then
          break
        fi
        sleep 5
      done

      if ! curl -fsS "http://127.0.0.1/admin/health" -H "Host: $DOMAIN" \
            | jq -e '.cert_ready == true' > /dev/null 2>&1; then
        echo "Cert not ready after 30 min; aborting registration" >&2
        exit 1
      fi

      ATTEMPT=0
      while (( ATTEMPT < 8 )); do
        if curl -fsS -X POST "$PORTAL_CALLBACK_URL" \
             -H 'Content-Type: application/json' \
             --data "$(jq -n \
               --arg cid "$CLOUD_ID" \
               --arg et "$ENROLLMENT_TOKEN" \
               --arg cat "$CLOUD_ADMIN_TOKEN" \
               '{cloud_id:$cid, enrollment_token:$et, cloud_admin_token:$cat}')"; then
          echo "Registered with portal"
          exit 0
        fi
        sleep $(( 2 ** ATTEMPT ))
        ATTEMPT=$(( ATTEMPT + 1 ))
      done

      echo "Registration failed after $ATTEMPT attempts" >&2
      exit 1

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

  - path: /etc/systemd/system/thany-cloud-register.service
    owner: root:root
    permissions: "0644"
    content: |
      [Unit]
      Description=Register cloud with portal
      Requires=thany-cloud.service
      After=thany-cloud.service

      [Service]
      Type=oneshot
      RemainAfterExit=yes
      ExecStart=/usr/local/bin/register-with-portal.sh
      StandardOutput=append:/var/log/thany-cloud/register.log
      StandardError=append:/var/log/thany-cloud/register.log

      [Install]
      WantedBy=multi-user.target

  - path: /etc/ufw/applications.d/thany-cloud
    owner: root:root
    permissions: "0644"
    content: |
      [thany-cloud-web]
      title=Thany Cloud (HTTP/HTTPS)
      description=Caddy + LE
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
  - chmod 0600 /run/cloud-secrets/cloud_admin_token /run/cloud-secrets/jwt_signing_key

  - |
    set -a
    CLOUD_ADMIN_TOKEN=$(cat /run/cloud-secrets/cloud_admin_token)
    JWT_SIGNING_KEY=$(cat /run/cloud-secrets/jwt_signing_key)
    set +a
    envsubst < /etc/thany-cloud/cloud.env.tpl > /opt/thany-cloud/.env
    chown ${admin_user}:${admin_user} /opt/thany-cloud/.env
    chmod 0640 /opt/thany-cloud/.env

  - curl -fsSL "${compose_url}" -o /opt/thany-cloud/docker-compose.yml
  - curl -fsSL "${caddyfile_url}" -o /etc/thany-cloud/Caddyfile.tpl.remote || true
  - curl -fsSL "${nginx_conf_url}" -o /opt/thany-cloud/nginx.conf
  - chown ${admin_user}:${admin_user} /opt/thany-cloud/docker-compose.yml /opt/thany-cloud/nginx.conf

  - |
    set -a
    # shellcheck disable=SC1091
    source /opt/thany-cloud/.env
    set +a
    envsubst '$DOMAIN $LE_EMAIL' < /etc/thany-cloud/Caddyfile.tpl > /opt/thany-cloud/Caddyfile
    if [ -n "${le_acme_ca}" ]; then
      sed -i "s|# ACME_CA_PLACEHOLDER|acme_ca ${le_acme_ca}|" /opt/thany-cloud/Caddyfile
    fi
    chown ${admin_user}:${admin_user} /opt/thany-cloud/Caddyfile

  - systemctl daemon-reload
  - systemctl enable --now thany-cloud.service
  - systemctl enable --now thany-cloud-register.service

  - shred -u /run/cloud-secrets/cloud_admin_token /run/cloud-secrets/jwt_signing_key || true

  - systemctl restart ssh
  - systemctl enable --now unattended-upgrades
  - systemctl restart fail2ban || true

final_message: "Cloud-init completed in $UPTIME seconds. Caddy + registration running async."
