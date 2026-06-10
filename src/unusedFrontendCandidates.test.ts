import { describe, expect, it } from 'vitest';

const sourceModules = import.meta.glob('./**/*.{ts,tsx}', {
  eager: true,
  import: 'default',
  query: '?raw',
}) as Record<string, string>;

const removedUnusedCandidates = [
  './components/Calendar/SummaryCards.tsx',
  './components/Contracts/ContractCreateForm.tsx',
  './components/LegalEntities/LegalEntityInlineEditor.tsx',
  './components/ui/Hint.tsx',
  './hooks/useClientStats.ts',
];

describe('unused frontend candidates cleanup', () => {
  it('keeps confirmed unused legacy candidates removed', () => {
    expect(Object.keys(sourceModules).filter((filePath) => removedUnusedCandidates.includes(filePath))).toEqual([]);
  });
});
