# PayPlanner deploy runbook

## Production TLS and HSTS

Production TLS is owned by the public web layer, not by the ASP.NET application. The expected production topology is:

- nginx terminates HTTPS for `crm.elitpravo.com` and `api.elitpravo.com`;
- nginx redirects public HTTP requests to HTTPS;
- nginx sends the `Strict-Transport-Security` header for public HTTPS responses after certificate validation;
- the backend process listens only on the private loopback HTTP endpoint `127.0.0.1:5080`;
- the backend port is not exposed directly to the Internet.

Because TLS is terminated before requests reach Kestrel, `Program.cs` intentionally does not call `UseHttpsRedirection()` or `UseHsts()`. Add those middleware calls only if the deployment topology changes and Kestrel becomes responsible for public HTTPS.

Before or after deployment, verify the public layer:

```bash
curl -I http://crm.elitpravo.com/
curl -I https://crm.elitpravo.com/
curl -I https://api.elitpravo.com/api/health
```

Expected result:

- HTTP responses redirect to HTTPS;
- HTTPS responses include `Strict-Transport-Security`;
- API health is checked through the public HTTPS URL and through the internal `http://127.0.0.1:5080/api/health` URL used by the deploy workflow.

## Realtime WebSocket Proxy

SignalR realtime channels are part of the public API surface:

- `/hubs/notifications` sends instant user notifications, badge refresh signals, and toast events;
- `/hubs/notes` synchronizes sticky note boards between users.

The public `api.elitpravo.com` nginx server must proxy these hub paths to the backend with WebSocket upgrade support. Keep HTTP fallback available, but do not rely on fallback as the main production transport.

Required nginx map in the `http` context:

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    '' close;
}
```

Required proxy settings for API locations that include `/hubs/notifications` and `/hubs/notes`:

```nginx
access_log off;
error_log /var/log/nginx/payplanner-hubs-error.log crit;
proxy_http_version 1.1;
proxy_set_header Upgrade $http_upgrade;
proxy_set_header Connection $connection_upgrade;
proxy_set_header Host $host;
proxy_set_header X-Real-IP $remote_addr;
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
proxy_set_header X-Forwarded-Proto $scheme;
proxy_read_timeout 3600s;
proxy_send_timeout 3600s;
```

Example dedicated locations:

```nginx
location /hubs/notifications {
    access_log off;
    error_log /var/log/nginx/payplanner-hubs-error.log crit;
    proxy_pass http://127.0.0.1:5080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 3600s;
    proxy_send_timeout 3600s;
}

location /hubs/notes {
    access_log off;
    error_log /var/log/nginx/payplanner-hubs-error.log crit;
    proxy_pass http://127.0.0.1:5080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 3600s;
    proxy_send_timeout 3600s;
}
```

Do not write regular access logs for the SignalR hub locations. Browser WebSocket connections can carry short-lived authentication data in the query string, and nginx access or upstream error logs would otherwise preserve the full URL during reconnects or backend restarts. Keep a separate hub error log at `crit` level so critical nginx failures are still visible without recording routine connection resets.

If old nginx logs were written before this rule was applied, sanitize them in place and keep the rest of each log line for diagnostics:

```bash
sudo find /var/log/nginx -maxdepth 1 -type f \( -name 'access.log*' -o -name 'error.log*' -o -name 'payplanner-hubs-error.log*' \) \
  -exec sh -c '
    for file do
      case "$file" in
        *.gz)
          tmp="$(mktemp)"
          gzip -dc "$file" | sed -E "s/access_token=([^&\" ]+)/signalr_token_redacted/g" > "$tmp"
          gzip -c "$tmp" > "$file.tmp" && cat "$file.tmp" > "$file"
          rm -f "$tmp" "$file.tmp"
          ;;
        *)
          sed -E -i "s/access_token=([^&\" ]+)/signalr_token_redacted/g" "$file"
          ;;
      esac
    done
  ' sh {} +
```

After sanitizing, confirm that no raw hub access-token parameter remains:

```bash
sudo grep -R "access_token=" /var/log/nginx/access.log /var/log/nginx/error.log /var/log/nginx/payplanner-hubs-error.log 2>/dev/null || true
sudo zgrep "access_token=" /var/log/nginx/access.log.*.gz /var/log/nginx/error.log.*.gz 2>/dev/null || true
```

After editing nginx on VPS:

```bash
sudo nginx -t
sudo systemctl reload nginx
```

Production acceptance checks:

1. Open CRM in a real browser with an active user session.
2. In DevTools Network, confirm that `/hubs/notifications` connects with WebSocket transport and receives `101 Switching Protocols`.
3. Open the Notes section and confirm that `/hubs/notes` also connects with WebSocket transport.
4. Create a test notification or note change from another session and confirm that it appears without manual refresh.

Automated readiness check after deploy:

```bash
PAYPLANNER_PRODUCTION_SMOKE_TOKEN="<short-lived jwt>" node scripts/run-notification-production-readiness.mjs \
  --api-url=https://api.elitpravo.com \
  --frontend-url=https://crm.elitpravo.com \
  --require-websocket=true
```

The smoke command checks public API health, `payplanner-push-worker.js`, and WebSocket `101 Switching Protocols` for `/hubs/notifications` and `/hubs/notes`. Use a short-lived JWT for a real CRM user and revoke or let it expire after the check.

The deploy artifact also contains `deploy-revision.txt` in the public frontend root. It stores the Git commit SHA that was packaged by GitHub Actions. After deploy, use it to verify that the public CRM is serving the expected revision, not only a healthy older release:

```bash
node scripts/run-production-deploy-readiness.mjs \
  --api-url=https://api.elitpravo.com \
  --frontend-url=https://crm.elitpravo.com \
  --expected-sha="$(git rev-parse HEAD)" \
  --allow-websocket-auth-challenge=true \
  --require-websocket=true
```

The GitHub Actions deploy workflow runs this production deploy readiness script after the VPS deploy, but does not depend on a short-lived repository JWT. In CI, the script checks the public frontend, the public revision marker, API health, the service worker, and the protected WebSocket hubs through the expected unauthenticated `401/403` auth challenge. For a full manual `101 Switching Protocols` check, run `run-notification-production-readiness.mjs` with a fresh short-lived JWT.

## Required backend configuration

Runtime secrets must come from environment variables, systemd environment files, user secrets, or a secret store. Do not commit real secret values to `appsettings*.json`.

Required production values:

- `ConnectionStrings__Default`;
- `Jwt__Secret` with at least 32 characters;
- `Cors__AllowedOrigins__0` and additional numbered origins when browser clients call the API cross-origin.

Web Push production values:

- `WebPush__Enabled` controls the browser push channel;
- `WebPush__PublicKey` is the public VAPID key returned to browsers;
- `WebPush__PrivateKey` is the private VAPID key used only by the backend;
- `WebPush__Subject` is the VAPID contact value, for example `mailto:admin@example.com`.

The deploy workflow reads the GitHub repository variable `PAYPLANNER_WEBPUSH_ENABLED` and secrets `PAYPLANNER_WEBPUSH_PUBLIC_KEY`, `PAYPLANNER_WEBPUSH_PRIVATE_KEY`, `PAYPLANNER_WEBPUSH_SUBJECT`. Web Push is disabled by default in CI/CD until `PAYPLANNER_WEBPUSH_ENABLED=true` is set explicitly. On VPS the workflow writes only the runtime `WebPush__*` values to `/opt/payplanner/shared/payplanner.env` with restricted permissions and connects that file through the systemd drop-in `/etc/systemd/system/payplanner.service.d/20-payplanner-runtime-env.conf`. If Web Push is explicitly enabled and any key or subject is missing, deployment must stop before the backend restart.

Optional integration values such as DaData and SMTP credentials are required only when the corresponding integrations are enabled.

## Secrets

Store production secrets outside the repository and outside build artifacts. The expected sources are GitHub Actions secrets for deploy-time values and a server-side environment file or secret store for runtime values.

Minimum runtime secrets and sensitive settings:

- `ConnectionStrings__Default`;
- `Jwt__Secret`;
- `WebPush__PrivateKey` when browser push notifications are enabled;
- optional `DaData__ApiKey` and `DaData__Secret`;
- optional SMTP password values;
- optional S3/Yandex Disk credentials when backup uploads are enabled.

Rotate `Jwt__Secret` and integration credentials through the secret store, then restart the backend service. Do not put real values into `backend/appsettings.json`, `backend/appsettings.Development.json`, or `backend/appsettings.Production.json`.

## CORS Origins

Development can use broad CORS for local ports. Production must use explicit origins:

```bash
Cors__AllowedOrigins__0=https://crm.elitpravo.com
Cors__AllowedOrigins__1=https://api.elitpravo.com
```

If browser clients and API are served from the same origin, CORS is not required for those same-origin requests. If `Cors:AllowedOrigins` is empty in Production, the backend does not enable wildcard cross-origin access.

## Startup Migrations And Release Sync

The backend startup sequence runs data maintenance before accepting traffic:

- database migrations;
- seed data;
- app release sync from `backend/AppReleases/releases.json`;
- configured backfills and startup maintenance steps.

Startup logs include start/success/failure messages and duration for these steps. If startup fails during migrations, seed, release sync, or maintenance, stop the rollout, inspect the failing step in logs, and decide whether to fix forward or restore from backup.

## Backup And Rollback

The deploy workflow creates a pre-release backup directory under `/opt/payplanner/deploy-backups` and keeps shared database backups under `/opt/payplanner/shared/backups`.

Before production deploy:

1. Confirm that a recent database backup exists.
2. Confirm that the backend artifact and frontend artifact are available.
3. Confirm that rollback target paths exist for `/opt/payplanner/app/backend` and `/var/www/crm.elitpravo.com`.

Rollback checklist:

1. Stop the backend service.
2. Restore the previous backend and frontend artifacts from the deploy backup.
3. Restore the database backup if migrations or startup maintenance changed data in a way that cannot be fixed forward.
4. Start the backend service.
5. Check `http://127.0.0.1:5080/api/health` locally and the public HTTPS health URL. Both checks must return JSON with `{"status":"ok"}`, not the CRM HTML shell.
6. Confirm that admins can open the CRM and that the What New module still loads release notes.

Do not manually delete production tables, columns, or backup files during rollback unless the recovery plan explicitly requires it.

## Health Checks

The deploy workflow checks the internal backend endpoint:

```bash
curl -fsS http://127.0.0.1:5080/api/health
curl -fsS http://127.0.0.1:5080/api/health | grep -Eq '"status"[[:space:]]*:[[:space:]]*"ok"'
```

Operators should also check public endpoints after nginx is serving the new frontend and proxying the API:

```bash
curl -I https://crm.elitpravo.com/
curl -fsS https://api.elitpravo.com/api/health
curl -fsS https://api.elitpravo.com/api/health | grep -Eq '"status"[[:space:]]*:[[:space:]]*"ok"'
```

Expected result:

- backend health returns HTTP 200;
- public CRM returns HTTPS;
- public API health returns HTTP 200 and JSON with `status: ok`;
- HSTS is present on public HTTPS responses after certificates are valid.
- `/opt/payplanner/app/REVISION` and `/opt/payplanner/app/.deploy-revision` contain the deployed Git revision.
