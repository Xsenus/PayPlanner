# Frontend hook-specific test coverage

Дата: 2026-06-10.

## Scope

Roadmap item: Milestone 9, "Добавить hook-specific tests для hooks без покрытия".

## Added coverage

- `src/hooks/useRolePermissions.test.tsx`
  - verifies default permission fallback before async load;
  - verifies role-specific permissions after API load;
  - verifies reload when `roleId` changes.
- `src/hooks/useActivityLogger.test.tsx`
  - verifies default payload merge and `Info` status fallback;
  - verifies incomplete activity events are skipped;
  - verifies API failures do not interrupt the UI flow.
- `src/hooks/useDebouncedValue.test.tsx`
  - verifies delayed update;
  - verifies previous pending updates are cancelled when value changes again.

Existing related coverage kept in the targeted run:

- `src/hooks/usePersistedTableSettings.test.tsx` for storage-backed table settings;
- `src/hooks/useUserTimeZone.test.tsx` for profile timezone loading/fallback.

## Targeted checks

```powershell
npx vitest run src/hooks/useDebouncedValue.test.tsx src/hooks/useActivityLogger.test.tsx src/hooks/useRolePermissions.test.tsx src/hooks/usePersistedTableSettings.test.tsx src/hooks/useUserTimeZone.test.tsx --no-file-parallelism
```

Result: 5 files passed, 12 tests passed.
