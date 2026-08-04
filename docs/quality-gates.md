# PayPlanner Quality Gates

Run these checks from the repository root before a PR, deploy, or roadmap checkbox that claims the app is ready.

## Standard Local Gate

```powershell
npm ci
npm run lint
npx tsc -p tsconfig.app.json --noEmit
npm run test:ci
npm run test:integration
npm run audit:dates:test
npm run audit:encoding
npm run audit:surface:test
npm run audit:surface
npm run build
dotnet test backend\PayPlanner.Api.sln --no-restore -c Debug
dotnet format backend\PayPlanner.Api.sln --verify-no-changes --no-restore --verbosity minimal
dotnet list backend\PayPlanner.Api.sln package --vulnerable --include-transitive
npm audit --audit-level=moderate
```

`npm run lint` can finish with warnings while still returning exit code 0. Treat new warnings in touched files as work to fix or explain.

## Targeted Checks

Use targeted tests while making small changes, then run the standard gate before committing or deploying:

- Backend controller/service change: `dotnet test backend\PayPlanner.Api.Tests\PayPlanner.Api.Tests.csproj --filter "FullyQualifiedName~<TestClassOrArea>"`.
- Frontend routing or permission change: `npm run test:guard`.
- Frontend component or hook change: run the closest Vitest file with `npx vitest run <path>`.
- Date/time behavior change: `npm run audit:dates:test` plus the relevant unit tests.
- New or removed frontend section, HTML form, controller, endpoint, or background service: `npm run audit:surface:update`, then `npm run audit:surface:test` and `npm run audit:surface`.
- Visual or responsive change: `npm run test:visual-smoke` or a focused browser smoke on the changed screen.
- File upload/download change: `npm run test:browser-downloads` plus backend upload/download tests.

## CI Gate Before Deploy

The deploy workflow must keep verification steps before `dotnet publish`, `npm run build`, artifact upload, and VPS deploy. The required pre-deploy checks are:

- backend restore;
- backend format verify;
- backend vulnerable package audit;
- backend tests;
- `npm ci`;
- frontend lint;
- frontend typecheck;
- date/time audit self-test;
- source encoding audit;
- system surface parser self-test and inventory freshness check;
- frontend package audit;
- frontend guard and unit tests;
- frontend integration tests;
- strict legal feature flag readiness.

Do not weaken the gate to unblock a deploy. Fix the failing check or document a deliberate, reviewed exception.
