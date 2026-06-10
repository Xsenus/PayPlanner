# Frontend component extraction 2026-06-10

## Scope

- Started Milestone 10 frontend decomposition with the `Reports` hotspot.
- Extracted pure report presentation helpers from `src/components/Reports/Reports.tsx` to `src/utils/reportPresentation.ts`.
- Kept UI rendering, API calls, hooks and report export behavior unchanged.

## Test Coverage

- `src/utils/reportPresentation.test.ts` covers currency labels, month labels, reversed month ranges, inclusive date ranges and deadline offset labels.
- Targeted command:

```powershell
npx vitest run src\utils\reportPresentation.test.ts src\utils\reportPeriod.test.ts src\utils\reportDateFormatting.test.ts --no-file-parallelism
```
