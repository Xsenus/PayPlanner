# Backend Controller Extraction - 2026-06-10

## Scope

- `ReportsController` no longer owns report period/date normalization helpers.
- Shared report query/date helper behavior is isolated in `ReportQueryPeriodResolver`.
- API routes, DTOs, authorization checks, query parameters, and response contracts were not changed.

## Coverage

- Added `ReportQueryPeriodResolverTests` for default report periods, reversed date ranges, date kind normalization, UTC instant normalization, and profile-local date conversion.
- Re-ran the existing `ReportsControllerTests` suite together with the new helper tests to verify report endpoint behavior stayed stable.

## Verification

```powershell
dotnet test backend\PayPlanner.Api.Tests\PayPlanner.Api.Tests.csproj --filter "FullyQualifiedName~ReportQueryPeriodResolverTests"
dotnet test backend\PayPlanner.Api.Tests\PayPlanner.Api.Tests.csproj --filter "FullyQualifiedName~ReportQueryPeriodResolverTests|FullyQualifiedName~ReportsControllerTests"
```
