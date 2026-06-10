# Package Name Metadata - 2026-06-10

## Scope

- Frontend package metadata now uses the product package name `payplanner`.
- `package-lock.json` was regenerated with the same dependency tree so `npm ci` remains reproducible.

## Coverage

- Added `PackageMetadataTests` to keep `package.json` and the root `package-lock.json` package names aligned.
- The test also prevents the starter package name from returning to tracked package metadata.

## Verification

```powershell
dotnet test backend\PayPlanner.Api.Tests\PayPlanner.Api.Tests.csproj --filter "FullyQualifiedName~PackageMetadataTests"
npm install --package-lock-only --ignore-scripts
```
