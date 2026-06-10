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
