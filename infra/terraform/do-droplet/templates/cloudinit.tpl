#cloud-config
bootcmd:
  - mkdir -p ${n8n_root_dir}

package_update: true
package_upgrade: true
packages:
  - unattended-upgrades
  - ufw
  - fail2ban
  - htop
  - curl
  - gnupg
  - nginx

users:
  - default
  - name: ${admin_user}
    groups: [adm, sudo]
    shell: /bin/bash
    lock_passwd: true
    sudo: "ALL=(ALL) NOPASSWD:ALL"
    ssh_authorized_keys:
%{ if length(extra_ssh_keys) > 0 ~}
${extra_ssh_keys}
%{ else ~}
      # none
%{ endif ~} 

ssh_pwauth: false

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
      AllowTcpForwarding yes
      AllowAgentForwarding yes
      AllowUsers ${admin_user}

  - path: /etc/nginx/sites-available/${domain_name}
    owner: root:root
    permissions: "0644"
    content: |
      server {
          listen 80;
          listen [::]:80;

          server_name ${domain_name};

          location / {
              proxy_pass http://localhost:${n8n_port};
              proxy_set_header Host $host;
              proxy_set_header X-Real-IP $remote_addr;
              proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
              proxy_set_header X-Forwarded-Proto $scheme;
              proxy_http_version 1.1;
              proxy_set_header Upgrade $http_upgrade;
              proxy_set_header Connection "upgrade";
          }
      }

  - path: /etc/ufw/applications.d/n8n
    owner: root:root
    permissions: "0644"
    content: |
      [n8n]
      title=Automation Engine
      description=Self-hosted n8n
      ports=5678/tcp

  - path: ${n8n_root_dir}/.env
    owner: ${admin_user}:${admin_user}
    permissions: "0640"
    content: |
      GENERIC_TIMEZONE=${n8n_timezone}
      TZ=${n8n_timezone}
      EDITOR=vim
      N8N_DIAGNOSTICS_ENABLED=false
      N8N_HIRING_BANNER_ENABLED=false
      N8N_USER_FOLDER=/home/node/.n8n

  - path: ${n8n_root_dir}/docker-compose.yml
    owner: ${admin_user}:${admin_user}
    permissions: "0644"
    defer: true
    content: |
      version: '3.7'
      services:
        n8n:
          image: docker.io/n8nio/n8n:${n8n_version}
          restart: unless-stopped
          env_file:
            - .env
          ports:
            - "${n8n_port}:5678"
          volumes:
            - ./data:/home/node/.n8n
      networks:
        default:
          name: n8n-net
          driver: bridge

  - path: /etc/systemd/system/n8n-compose.service
    owner: ${admin_user}:${admin_user}
    permissions: "0644"
    defer: true
    content: |
      [Unit]
      Description=n8n Docker Compose Service
      Requires=docker.service
      After=docker.service network-online.target
      Wants=network-online.target

      [Service]
      Type=oneshot
      RemainAfterExit=yes
      WorkingDirectory=${n8n_root_dir}
      Environment=COMPOSE_PROJECT_NAME=n8n
      User=${admin_user}
      ExecStart=/usr/bin/docker compose up -d
      ExecStop=/usr/bin/docker compose down
      Restart=on-failure
      RestartSec=5s
      TimeoutStartSec=120
      TimeoutStopSec=60

      [Install]
      WantedBy=multi-user.target

  - path: /etc/apt/apt.conf.d/50unattended-upgrades
    owner: root:root
    permissions: "0644"
    content: |
      Unattended-Upgrade::Allowed-Origins {
              "$${distro_id}:$${distro_codename}";
              "$${distro_id}:$${distro_codename}-security";
              "$${distro_id}:$${distro_codename}-updates";
              "$${distro_id}:$${distro_codename}-backports";
      };
      Unattended-Upgrade::Automatic-Reboot "true";
      Unattended-Upgrade::Automatic-Reboot-Time "03:30";

runcmd:
  - ufw default deny incoming
  - ufw default allow outgoing
  - ufw limit OpenSSH
%{ for p in additional_tcp ~}
  - ufw allow ${p}/tcp
%{ endfor ~}
  - ufw allow 'Nginx Full'
  - ufw --force enable
  - apt-get update
  - apt-get install -y ca-certificates curl gnupg
  - install -m 0755 -d /etc/apt/keyrings
  - curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
  - chmod a+r /etc/apt/keyrings/docker.gpg
  - |
    echo \
      "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu \
      $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | \
      tee /etc/apt/sources.list.d/docker.list > /dev/null
  - apt-get update
  - apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  - systemctl enable --now docker
  - usermod -aG docker ${admin_user}
  - install -d -o ${admin_user} -g ${admin_user} -m 0755 ${n8n_root_dir} ${n8n_root_dir}/data
  - chown -R ${admin_user}:${admin_user} ${n8n_root_dir}
  - ln -s /etc/nginx/sites-available/${domain_name} /etc/nginx/sites-enabled/
  - rm -f /etc/nginx/sites-enabled/default
  - systemctl daemon-reload
  - systemctl enable --now n8n-compose.service
  - systemctl restart nginx
  - systemctl restart ssh
  - systemctl enable --now unattended-upgrades
  - systemctl restart fail2ban || true
  - apt-get install -y certbot python3-certbot-nginx
  - sleep 60
  - |
    if [ ! -d /etc/letsencrypt/live/${domain_name} ]; then
      certbot --nginx \
        -d ${domain_name} \
        --non-interactive \
        --agree-tos \
        -m ${certbot_email} \
        --redirect
    fi
    - |
    if [ ! -f /etc/nginx/dhparam.pem ]; then
      openssl dhparam -out /etc/nginx/dhparam.pem 2048
    fi
  - |
    NGINX_CONF="/etc/nginx/sites-available/${domain_name}"
    sed -i '/ssl_certificate_key/a add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;' "$NGINX_CONF"
    sed -i 's|ssl_dhparam /etc/letsencrypt/ssl-dhparams.pem;|ssl_dhparam /etc/nginx/dhparam.pem;|' "$NGINX_CONF"
    sed -i 's/listen 443 ssl;/listen 443 ssl http2;/' "$NGINX_CONF"
    sed -i 's/listen \[::\]:443 ssl ipv6only=on;/listen \[::\]:443 ssl http2 ipv6only=on;/' "$NGINX_CONF"
  - nginx -t 
  - systemctl reload nginx

final_message: "Cloud-init completed in $UPTIME seconds"
