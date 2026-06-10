# Legal feature flag CI gate

Дата: 2026-06-10.

## Scope

Roadmap item: Milestone 8, "Добавить feature flag tests для legal/calendar integrations".

## Coverage

- `test:legal-flags:strict` uses `scripts/run-legal-readiness-report.mjs --public-only --skip-e2e --skip-load --fail-on-enabled-legal-flags`.
- `.github/workflows/deploy.yml` runs the strict gate before backend publish and frontend build artifacts are packaged.
- `LegalFeatureFlagCiGateTests` protects the CI wiring, the strict script options, and disabled-by-default flags in `backend/appsettings.json`.

## Targeted checks

```powershell
dotnet test backend\PayPlanner.Api.Tests\PayPlanner.Api.Tests.csproj --filter LegalFeatureFlagCiGateTests
$env:PAYPLANNER_READINESS_API_URL='https://api.elitpravo.com/api'; npm run test:legal-flags:strict
```

Result: backend contract tests passed; readiness strict gate passed with no enabled legal flags.
