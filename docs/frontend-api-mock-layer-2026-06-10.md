# Frontend shared API mock layer

Дата: 2026-06-10.

## Scope

Roadmap item: Milestone 9, "Ввести shared API mock layer или MSW".

## Implementation

- Added `src/test/apiMock.ts` as the shared mock layer for frontend tests that mock `src/services/api`.
- The helper exposes a lazy `apiServiceMock` proxy, plus `resetApiServiceMock`, `clearApiServiceMock`, and `apiMockMethod`.
- Migrated first tests to the shared layer:
  - `src/components/Settings/FeatureFlagSettings.test.tsx`;
  - `src/components/Settings/UserOnlineSettings.test.tsx`.
- Added `src/test/apiMock.test.ts` to lock reset/clear behavior.

## Migration rule

New tests that mock `apiService` should use `src/test/apiMock.ts`. Existing ad hoc mocks can be migrated gradually while touching their test files.

## Targeted checks

```powershell
npx vitest run src/test/apiMock.test.ts src/components/Settings/FeatureFlagSettings.test.tsx src/components/Settings/UserOnlineSettings.test.tsx --no-file-parallelism
npx tsc -p tsconfig.app.json --noEmit
npm run lint
```

Result: targeted tests passed; TypeScript passed; lint reported 0 errors and the existing warning set.
