#cloud-config
bootcmd:
  - mkdir -p /opt/thany-cloud /opt/thany-cloud/puller /etc/thany-cloud /var/log/thany-cloud /run/cloud-secrets
  - chmod 0700 /run/cloud-secrets

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
      STORAGE_PROVIDER=${storage_provider}
      STORAGE_ENDPOINT=${storage_endpoint}
      STORAGE_REGION=${storage_region}
      STORAGE_BUCKET=${storage_bucket}
      STORAGE_ACCESS_KEY_ID=${storage_access_key_id}
      STORAGE_ACCESS_KEY_SECRET=${storage_access_secret}
      CLOUD_ADMIN_TOKEN=$${CLOUD_ADMIN_TOKEN}
      JWT_SIGNING_KEY=$${JWT_SIGNING_KEY}
      POSTGRES_PASSWORD=$${POSTGRES_PASSWORD}
      OLLAMA_VISION_PULL_TAG=${ollama_vision_pull_tag}
      OLLAMA_TEXT_PULL_TAG=${ollama_text_pull_tag}
      OLLAMA_TEXT_IMAGE_TAG=${ollama_text_image_tag}

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
              proxy_set_header Host $host;
              proxy_set_header X-Real-IP $remote_addr;
              proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
              proxy_set_header X-Forwarded-Proto $scheme;
              proxy_http_version 1.1;
              proxy_set_header Upgrade $http_upgrade;
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

  - path: /opt/thany-cloud/puller/run.sh
    owner: root:root
    permissions: "0755"
    content: |
      #!/bin/sh
      set -eu
      : "$${OLLAMA_PULL_TAG:?puller: OLLAMA_PULL_TAG must be set}"
      echo "[puller] starting ollama serve"
      ollama serve &
      SERVE_PID=$!
      trap 'kill -TERM $SERVE_PID 2>/dev/null || true; wait $SERVE_PID 2>/dev/null || true' EXIT
      echo "[puller] waiting for ollama to accept connections (max 60s)"
      ready=0
      i=0
      while [ "$i" -lt 60 ]; do
        if ollama list >/dev/null 2>&1; then ready=1; break; fi
        sleep 1
        i=$((i+1))
      done
      if [ "$ready" != "1" ]; then
        echo "[puller] FATAL: ollama serve never came up" >&2
        exit 1
      fi
      echo "[puller] pulling $OLLAMA_PULL_TAG"
      pulled=0
      for attempt in 1 2 3 4 5; do
        if ollama pull "$OLLAMA_PULL_TAG"; then pulled=1; break; fi
        echo "[puller] ollama pull attempt $attempt failed; sleeping $((15*attempt))s..." >&2
        sleep $((15 * attempt))
      done
      if [ "$pulled" != "1" ]; then
        echo "[puller] FATAL: ollama pull failed after 5 attempts" >&2
        exit 1
      fi
      echo "[puller] verifying model is present after pull"
      MODEL_NAME=$${OLLAMA_PULL_TAG%:*}
      ollama list | grep -q "$MODEL_NAME" || { echo "[puller] FATAL: model missing from ollama list after pull" >&2; exit 1; }
      echo "[puller] done"

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
            Storage__Provider: $${STORAGE_PROVIDER}
            Storage__Endpoint: $${STORAGE_ENDPOINT}
            Storage__Region: $${STORAGE_REGION}
            Storage__Bucket: $${STORAGE_BUCKET}
            Storage__AccessKeyId: $${STORAGE_ACCESS_KEY_ID}
            Storage__AccessKeySecret: $${STORAGE_ACCESS_KEY_SECRET}
            Cert__LiveDir: /etc/letsencrypt/live/$${DOMAIN}
            IngestSaga__Sidecars__OllamaVision__BaseUrl: http://ollama-vision:11434
            IngestSaga__Sidecars__OllamaVision__HealthPath: /api/version
            IngestSaga__Sidecars__OllamaVision__RequestTimeoutSeconds: "1200"
            IngestSaga__Sidecars__OllamaText__BaseUrl: http://ollama-text:11434
            IngestSaga__Sidecars__OllamaText__HealthPath: /api/version
            IngestSaga__Sidecars__OllamaText__RequestTimeoutSeconds: "900"
            IngestSaga__Models__Vlm__OllamaTag:    $${OLLAMA_VISION_PULL_TAG}
            IngestSaga__Models__Route__OllamaTag:  $${OLLAMA_TEXT_PULL_TAG}
            IngestSaga__Models__Entity__OllamaTag: $${OLLAMA_TEXT_PULL_TAG}
            IngestSaga__Sidecars__Docling__BaseUrl: http://docling:5001
            IngestSaga__Sidecars__Docling__HealthPath: /health
            IngestSaga__Sidecars__Parakeet__BaseUrl: http://parakeet:5092
            IngestSaga__Sidecars__Parakeet__HealthPath: /health
          volumes:
            - /etc/letsencrypt:/etc/letsencrypt:ro
          depends_on:
            postgres:      { condition: service_healthy }
            ollama-vision: { condition: service_started }
            ollama-text:   { condition: service_started }
            docling:       { condition: service_started }
            parakeet:      { condition: service_started }
          networks: [cloud]
          healthcheck:
            test: ["CMD-SHELL", "wget -q -O /dev/null http://localhost:8080/health/live || exit 1"]
            interval: 10s
            timeout: 5s
            retries: 5

        postgres:
          image: pgvector/pgvector:pg16
          restart: unless-stopped
          environment:
            POSTGRES_USER: cloud
            POSTGRES_PASSWORD: $${POSTGRES_PASSWORD}
            POSTGRES_DB: cloud
          volumes:
            - /mnt/thany-data/postgres:/var/lib/postgresql/data
          networks: [cloud]
          healthcheck:
            test: ["CMD-SHELL", "pg_isready -U cloud -d cloud"]
            interval: 5s
            timeout: 5s
            retries: 10

        ollama-vision:
          image: ollama/ollama:$${OLLAMA_TEXT_IMAGE_TAG:-0.24.0}
          restart: unless-stopped
          environment:
            OLLAMA_HOST: "0.0.0.0:11434"
            OLLAMA_KEEP_ALIVE: "0"
            OLLAMA_NUM_PARALLEL: "1"
            OLLAMA_MAX_LOADED_MODELS: "1"
          volumes:
            - ollama-vision-models:/root/.ollama
          networks: [cloud]
          mem_limit: 6g
          healthcheck:
            test: ["CMD", "/bin/ollama", "list"]
            interval: 15s
            timeout: 5s
            retries: 10
            start_period: 30s

        ollama-text:
          image: ollama/ollama:$${OLLAMA_TEXT_IMAGE_TAG:-0.24.0}
          restart: unless-stopped
          environment:
            OLLAMA_HOST: "0.0.0.0:11434"
            OLLAMA_KEEP_ALIVE: "0"
            OLLAMA_NUM_PARALLEL: "1"
            OLLAMA_MAX_LOADED_MODELS: "1"
          volumes:
            - ollama-text-models:/root/.ollama
          networks: [cloud]
          mem_limit: 4g
          healthcheck:
            test: ["CMD", "/bin/ollama", "list"]
            interval: 15s
            timeout: 5s
            retries: 10
            start_period: 30s

        ollama-vision-puller:
          image: ollama/ollama:$${OLLAMA_TEXT_IMAGE_TAG:-0.24.0}
          profiles: ["init"]
          restart: "no"
          entrypoint: ["/bin/sh", "/opt/puller/run.sh"]
          environment:
            OLLAMA_HOST: "127.0.0.1:11434"
            OLLAMA_PULL_TAG: $${OLLAMA_VISION_PULL_TAG:-qwen3-vl:4b}
          volumes:
            - ollama-vision-models:/root/.ollama
            - /opt/thany-cloud/puller:/opt/puller:ro
          networks: [cloud]

        ollama-text-puller:
          image: ollama/ollama:$${OLLAMA_TEXT_IMAGE_TAG:-0.24.0}
          profiles: ["init"]
          restart: "no"
          entrypoint: ["/bin/sh", "/opt/puller/run.sh"]
          environment:
            OLLAMA_HOST: "127.0.0.1:11434"
            OLLAMA_PULL_TAG: $${OLLAMA_TEXT_PULL_TAG:-qwen3:1.7b-q4_K_M}
          volumes:
            - ollama-text-models:/root/.ollama
            - /opt/thany-cloud/puller:/opt/puller:ro
          networks: [cloud]

        docling:
          image: quay.io/docling-project/docling-serve-cpu:v1.18.0
          restart: unless-stopped
          environment:
            UVICORN_HOST: "0.0.0.0"
            UVICORN_PORT: "5001"
            UVICORN_WORKERS: "1"
            DOCLING_SERVE_LOG_LEVEL: "INFO"
            DOCLING_NUM_THREADS: "2"
          volumes:
            - docling-models:/opt/app-root/src/.cache
          networks: [cloud]
          mem_limit: 1500m
          healthcheck:
            test: ["CMD-SHELL", "wget -q -O /dev/null http://localhost:5001/health || exit 1"]
            interval: 15s
            timeout: 5s
            retries: 10
            start_period: 60s

        parakeet:
          image: ghcr.io/achetronic/parakeet:0.3.0-int8
          restart: unless-stopped
          command: ["-models", "/models", "-port", "5092", "-workers", "1"]
          networks: [cloud]
          mem_limit: 3500m

      volumes:
        ollama-vision-models:
        ollama-text-models:
        docling-models:

      networks:
        cloud:

runcmd:
  - |
    DATA_DEV="${data_device}"
    echo "[cloud-init] provisioning data volume at $DATA_DEV"
    for i in $(seq 1 60); do
      [ -b "$DATA_DEV" ] && break
      sleep 2
    done
    if [ ! -b "$DATA_DEV" ]; then
      echo "[cloud-init] FATAL: data volume $DATA_DEV never attached" >&2
      exit 1
    fi
    if ! blkid "$DATA_DEV" >/dev/null 2>&1; then
      echo "[cloud-init] formatting $DATA_DEV as ext4"
      mkfs.ext4 -F "$DATA_DEV" || { echo "[cloud-init] FATAL: mkfs failed" >&2; exit 1; }
    fi
    mkdir -p /mnt/thany-data
    grep -q " /mnt/thany-data " /etc/fstab \
      || echo "$DATA_DEV /mnt/thany-data ext4 defaults,nofail,discard 0 2" >> /etc/fstab
    mountpoint -q /mnt/thany-data || mount /mnt/thany-data \
      || { echo "[cloud-init] FATAL: mount of /mnt/thany-data failed" >&2; exit 1; }
    mkdir -p /mnt/thany-data/postgres
    touch /opt/thany-cloud/.data-volume-ok
    echo "[cloud-init] data volume mounted at /mnt/thany-data"

  - timedatectl set-timezone ${timezone}
  - ufw default deny incoming
  - ufw default allow outgoing
  - ufw limit OpenSSH
  - ufw allow thany-cloud-web
  - ufw --force enable

  - install -m 0755 -d /etc/apt/keyrings
  - |
    retry() {
      label=$1; shift
      for i in 1 2 3 4 5; do
        if "$@"; then return 0; fi
        echo "[cloud-init] $label attempt $i failed; sleeping $((10*i))s..." >&2
        sleep $((10 * i))
      done
      echo "[cloud-init] $label failed after 5 attempts" >&2
      return 1
    }
    fetch_docker_gpg() {
      (set -o pipefail; curl -fsSL https://download.docker.com/linux/ubuntu/gpg \
        | gpg --dearmor -o /etc/apt/keyrings/docker.gpg)
    }
    retry "docker gpg fetch" fetch_docker_gpg || exit 1
    chmod a+r /etc/apt/keyrings/docker.gpg
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo $VERSION_CODENAME) stable" \
      > /etc/apt/sources.list.d/docker.list
    retry "apt-get update" apt-get update || exit 1
    retry "apt-get install docker" apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin || exit 1
    usermod -aG docker ${admin_user}

  - |
    if [ ! -f /swapfile ]; then
      fallocate -l 4G /swapfile
      chmod 600 /swapfile
      mkswap /swapfile
      swapon /swapfile
      echo '/swapfile none swap sw 0 0' >> /etc/fstab
    fi

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

  - |
    cd /opt/thany-cloud
    pull_with_retry() {
      for i in 1 2 3 4 5; do
        if sudo -u ${admin_user} docker compose "$@" pull; then return 0; fi
        echo "[cloud-init] '$@' pull attempt $i failed; sleeping $((15*i))s..." >&2
        sleep $((15 * i))
      done
      echo "[cloud-init] '$@' pull failed after 5 attempts" >&2
      return 1
    }
    pull_with_retry || exit 1
    pull_with_retry --profile init || exit 1

  - |
    cd /opt/thany-cloud
    rm -f /opt/thany-cloud/.ollama-vision-puller-ok /opt/thany-cloud/.ollama-text-puller-ok
    sudo -u ${admin_user} docker compose --profile init run \
         --name thany-cloud-ollama-vision-puller-run \
         ollama-vision-puller > /var/log/thany-cloud/ollama-vision-puller.log 2>&1
    RC=$?
    if [ "$RC" = "0" ]; then
      touch /opt/thany-cloud/.ollama-vision-puller-ok
    else
      echo "[cloud-init] ollama-vision-puller FAILED rc=$RC; log at /var/log/thany-cloud/ollama-vision-puller.log; container retained as thany-cloud-ollama-vision-puller-run for 'docker logs'" >&2
    fi
    sudo -u ${admin_user} docker compose --profile init run \
         --name thany-cloud-ollama-text-puller-run \
         ollama-text-puller > /var/log/thany-cloud/ollama-text-puller.log 2>&1
    RC=$?
    if [ "$RC" = "0" ]; then
      touch /opt/thany-cloud/.ollama-text-puller-ok
    else
      echo "[cloud-init] ollama-text-puller FAILED rc=$RC; log at /var/log/thany-cloud/ollama-text-puller.log; container retained as thany-cloud-ollama-text-puller-run for 'docker logs'" >&2
    fi

  - systemctl daemon-reload
  - systemctl enable --now thany-cloud.service

  - |
    if [ ! -f /opt/thany-cloud/.data-volume-ok ] || [ ! -f /opt/thany-cloud/.ollama-vision-puller-ok ] || [ ! -f /opt/thany-cloud/.ollama-text-puller-ok ]; then
      echo "[cloud-init] skipping certbot + registration callback - data volume mount or an ollama puller did not complete; cloud will not register so portal sees failed provisioning" >&2
      exit 0
    fi
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

final_message: "Cloud-init completed in $UPTIME seconds. nginx + Cloud.Api + 3 sidecars running with models pre-pulled; registration fires on cert install."
