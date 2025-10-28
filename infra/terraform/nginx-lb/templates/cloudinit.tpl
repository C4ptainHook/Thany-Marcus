#cloud-config
package_update: true
package_upgrade: true
packages:
  - nginx
  - certbot
  - python3-certbot-nginx
  - ufw
  - fail2ban
  - curl
  - htop
  - apache2-utils

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

  - path: /etc/nginx/.htpasswd
    owner: www-data:www-data
    permissions: "0640"
    content: |
%{ if length(basic_auth_htpasswd) > 0 ~}
${basic_auth_htpasswd}
%{ else ~}
      # no users
%{ endif ~}

  - path: /etc/nginx/conf.d/rate-limiting.conf
    owner: root:root
    permissions: "0644"
    content: |
      limit_req_zone $binary_remote_addr zone=req_limit:10m rate=${rate_limit_req_per_sec}r/s;
      limit_conn_zone $binary_remote_addr zone=conn_limit:10m;

  - path: /etc/nginx/conf.d/security-headers.conf
    owner: root:root
    permissions: "0644"
    content: |
      add_header Strict-Transport-Security "max-age=31536000; includeSubDomains; preload" always;
      add_header X-Frame-Options "DENY" always;
      add_header X-Content-Type-Options "nosniff" always;
      add_header X-XSS-Protection "1; mode=block" always;
      add_header Referrer-Policy "strict-origin-when-cross-origin" always;
      add_header Permissions-Policy "geolocation=(), microphone=(), camera=()" always;
      server_tokens off;

  - path: /etc/nginx/conf.d/slowloris-protection.conf
    owner: root:root
    permissions: "0644"
    content: |
      client_body_timeout 10s;
      client_header_timeout 10s;
      keepalive_timeout 15s;
      send_timeout 10s;
      client_max_body_size 10m;

  - path: /etc/nginx/conf.d/stub-status.conf
    owner: root:root
    permissions: "0644"
    content: |
      server {
          listen 8080;
          server_name localhost;

          location /nginx_status {
              stub_status on;
              access_log off;
              allow 127.0.0.1;
              allow 10.0.0.0/8;
              deny all;
          }
      }

  - path: /etc/nginx/conf.d/upstream.conf
    owner: root:root
    permissions: "0644"
    content: |
      upstream backend {
          server ${backend_target}:80 max_fails=3 fail_timeout=30s;
          keepalive 32;
      }

  - path: /etc/nginx/sites-available/${fqdn}
    owner: root:root
    permissions: "0644"
    content: |
      server {
          listen 80;
          listen [::]:80;
          server_name ${fqdn};

          location /.well-known/acme-challenge/ {
              root /var/www/html;
          }

          limit_req zone=req_limit burst=${rate_limit_burst} nodelay;
          limit_conn conn_limit ${connection_limit};

          location / {
              proxy_pass http://backend;
              proxy_http_version 1.1;
              proxy_set_header Host $host;
              proxy_set_header X-Real-IP $remote_addr;
              proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
              proxy_set_header X-Forwarded-Proto $scheme;
              proxy_set_header Connection "";

              proxy_connect_timeout 5s;
              proxy_send_timeout 30s;
              proxy_read_timeout 30s;

              proxy_buffering off;
              proxy_request_buffering off;
          }

          location /health {
              access_log off;
              proxy_pass http://backend;
          }

          error_page 429 /429.html;
          location = /429.html {
              internal;
              default_type text/plain;
              return 429 "Rate limit exceeded. Please try again later.\n";
          }
      }

  - path: /etc/nginx/sites-available/${fqdn}-ssl
    owner: root:root
    permissions: "0644"
    content: |
      server {
          listen 80;
          listen [::]:80;
          server_name ${fqdn};

          location /.well-known/acme-challenge/ {
              root /var/www/html;
          }

          location / {
              return 301 https://$server_name$request_uri;
          }
      }

      server {
          listen 443 ssl http2;
          listen [::]:443 ssl http2;
          server_name ${fqdn};

          ssl_certificate /etc/letsencrypt/live/${fqdn}/fullchain.pem;
          ssl_certificate_key /etc/letsencrypt/live/${fqdn}/privkey.pem;
          ssl_protocols TLSv1.2 TLSv1.3;
          ssl_ciphers HIGH:!aNULL:!MD5;
          ssl_prefer_server_ciphers on;
          ssl_session_cache shared:SSL:10m;
          ssl_session_timeout 10m;

          limit_req zone=req_limit burst=${rate_limit_burst} nodelay;
          limit_conn conn_limit ${connection_limit};

          location / {
              proxy_pass http://backend;
              proxy_http_version 1.1;
              proxy_set_header Host $host;
              proxy_set_header X-Real-IP $remote_addr;
              proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
              proxy_set_header X-Forwarded-Proto $scheme;
              proxy_set_header Connection "";

              proxy_connect_timeout 5s;
              proxy_send_timeout 30s;
              proxy_read_timeout 30s;

              proxy_buffering off;
              proxy_request_buffering off;
          }

          location /health {
              access_log off;
              proxy_pass http://backend;
          }

          error_page 429 /429.html;
          location = /429.html {
              internal;
              default_type text/plain;
              return 429 "Rate limit exceeded. Please try again later.\n";
          }
      }

  - path: /etc/systemd/system/nginx-exporter.service
    owner: root:root
    permissions: "0644"
    content: |
      [Unit]
      Description=NGINX Prometheus Exporter
      After=network.target nginx.service
      Wants=nginx.service

      [Service]
      Type=simple
      User=nobody
      ExecStart=/usr/local/bin/nginx-prometheus-exporter -nginx.scrape-uri=http://127.0.0.1:8080/nginx_status
      Restart=on-failure
      RestartSec=5s

      [Install]
      WantedBy=multi-user.target

runcmd:
  - ufw default deny incoming
  - ufw default allow outgoing
  - ufw limit OpenSSH
  - ufw allow 80/tcp
  - ufw allow 443/tcp
  - ufw allow from 10.0.0.0/8 to any port 8080 proto tcp
  - ufw allow from 10.0.0.0/8 to any port 9113 proto tcp
  - ufw --force enable
  - systemctl enable --now fail2ban
  - ln -sf /etc/nginx/sites-available/${fqdn} /etc/nginx/sites-enabled/${fqdn}
  - rm -f /etc/nginx/sites-enabled/default
  - nginx -t && systemctl enable nginx && systemctl start nginx || echo "Nginx initial start failed"
  - sleep 10
  - certbot certonly --nginx -d ${fqdn} --non-interactive --agree-tos -m ${certbot_email} || echo "Certbot failed, staying on HTTP"
  - |
    if [ -f /etc/letsencrypt/live/${fqdn}/fullchain.pem ]; then
      ln -sf /etc/nginx/sites-available/${fqdn}-ssl /etc/nginx/sites-enabled/${fqdn}
      nginx -t && systemctl reload nginx || echo "SSL config failed"
    fi
  - wget https://github.com/nginxinc/nginx-prometheus-exporter/releases/download/v1.1.0/nginx-prometheus-exporter_1.1.0_linux_amd64.tar.gz -O /tmp/nginx-exporter.tar.gz
  - tar -xzf /tmp/nginx-exporter.tar.gz -C /tmp
  - mv /tmp/nginx-prometheus-exporter /usr/local/bin/
  - chmod +x /usr/local/bin/nginx-prometheus-exporter
  - rm /tmp/nginx-exporter.tar.gz
  - systemctl daemon-reload
  - systemctl enable --now nginx-exporter || echo "Exporter failed"
  - systemctl restart ssh

final_message: "nginx load balancer ready"
