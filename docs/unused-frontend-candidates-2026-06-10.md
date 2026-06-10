# Unused frontend candidates 2026-06-10

## Removed

The following candidate files were removed after manual `rg` / `git grep` checks found no runtime imports or route-level references:

- `src/components/Calendar/SummaryCards.tsx`
- `src/components/Contracts/ContractCreateForm.tsx`
- `src/components/LegalEntities/LegalEntityInlineEditor.tsx`
- `src/components/ui/Hint.tsx`
- `src/hooks/useClientStats.ts`

## Notes

- `ContractCreatePage.tsx` contains the active contract creation screen; `ContractCreateForm.tsx` was not imported by it.
- `useClients.ts` still exports the active `useClientStats` hook; the removed `src/hooks/useClientStats.ts` duplicate was not imported.
- `ClientDetail.tsx` has a local `actSummaryCards` variable, but it does not import `Calendar/SummaryCards.tsx`.

## Guard

`src/unusedFrontendCandidates.test.ts` keeps these confirmed legacy paths from being reintroduced accidentally.
