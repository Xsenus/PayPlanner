# Frontend accessibility test coverage 2026-06-10

## Scope

- Navigation landmark names and keyboard activation are covered by `src/components/Navigation/Navigation.test.tsx`.
- Modal dialog roles, `aria-modal`, focus trap, Tab wrapping, Shift+Tab wrapping and Escape close are covered by `src/components/ui/ModalAccessibility.test.tsx`.
- Payment toolbar role, accessible name and icon button labels are covered by `src/components/Payments/Payments.integration.test.tsx`.
- Positive `tabIndex` values in application components are blocked by `src/accessibilityContracts.test.ts`.

## Targeted command

```powershell
npx vitest run src\accessibilityContracts.test.ts src\components\Navigation\Navigation.test.tsx src\components\ui\ModalAccessibility.test.tsx src\components\Payments\Payments.integration.test.tsx --no-file-parallelism
```
