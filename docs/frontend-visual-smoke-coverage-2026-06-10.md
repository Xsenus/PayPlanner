# Frontend visual smoke coverage 2026-06-10

## Scope

- `scripts/run-visual-smoke.mjs` checks the frontend in desktop, tablet and mobile viewports.
- The smoke fails on page-level horizontal overflow, uncaught page errors and unexpected `console.error` messages.
- Screenshots are written to `artifacts/visual-smoke` by default.
- Without credentials the smoke checks the public shell. With `PAYPLANNER_VISUAL_TOKEN` or `PAYPLANNER_VISUAL_EMAIL` / `PAYPLANNER_VISUAL_PASSWORD`, it checks key working routes: calendar, tasks, task approvals, task calendar, payments and invoices.

## CI

- `.github/workflows/visual-smoke.yml` runs the smoke on a daily schedule and by manual dispatch.
- The workflow uploads screenshots as an artifact for review.

## Targeted commands

```powershell
npx vitest run scripts\run-visual-smoke.test.mjs --no-file-parallelism
npm run test:visual-smoke -- --frontend-url http://127.0.0.1:5174 --artifact-dir artifacts/visual-smoke-local --timeout-ms 20000
```
