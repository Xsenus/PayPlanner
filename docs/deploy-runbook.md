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

## Required backend configuration

Runtime secrets must come from environment variables, systemd environment files, user secrets, or a secret store. Do not commit real secret values to `appsettings*.json`.

Required production values:

- `ConnectionStrings__Default`;
- `Jwt__Secret` with at least 32 characters;
- `Cors__AllowedOrigins__0` and additional numbered origins when browser clients call the API cross-origin.

Optional integration values such as DaData and SMTP credentials are required only when the corresponding integrations are enabled.

## Secrets

Store production secrets outside the repository and outside build artifacts. The expected sources are GitHub Actions secrets for deploy-time values and a server-side environment file or secret store for runtime values.

Minimum runtime secrets and sensitive settings:

- `ConnectionStrings__Default`;
- `Jwt__Secret`;
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
5. Check `http://127.0.0.1:5080/api/health` locally and the public HTTPS health URL.
6. Confirm that admins can open the CRM and that the What New module still loads release notes.

Do not manually delete production tables, columns, or backup files during rollback unless the recovery plan explicitly requires it.

## Health Checks

The deploy workflow checks the internal backend endpoint:

```bash
curl -fsS http://127.0.0.1:5080/api/health
```

Operators should also check public endpoints after nginx is serving the new frontend and proxying the API:

```bash
curl -I https://crm.elitpravo.com/
curl -fsS https://api.elitpravo.com/api/health
```

Expected result:

- backend health returns HTTP 200;
- public CRM returns HTTPS;
- public API health returns HTTP 200;
- HSTS is present on public HTTPS responses after certificates are valid.
