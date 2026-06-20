import { describe, expect, it } from 'vitest';

const sourceModules = import.meta.glob('./components/**/*.{ts,tsx}', {
  eager: true,
  import: 'default',
  query: '?raw',
}) as Record<string, string>;

describe('dialog source contracts', () => {
  it('uses the CRM dialog instead of native browser confirmations', () => {
    const offenders = Object.entries(sourceModules)
      .filter(([filePath]) => !/\.test\.(tsx|ts)$/.test(filePath))
      .filter(([, source]) => /\bwindow\.confirm\s*\(/.test(source))
      .map(([filePath]) => filePath);

    expect(offenders).toEqual([]);
  });

  it('does not use native browser prompts in contract details', () => {
    const contractDetailSource = sourceModules['./components/Contracts/ContractDetailPage.tsx'];

    expect(contractDetailSource).toBeDefined();
    expect(contractDetailSource).not.toMatch(/\bwindow\.prompt\s*\(/);
  });

  it('does not use native browser prompts in act details', () => {
    const actDetailSource = sourceModules['./components/Acts/ActDetailPage.tsx'];

    expect(actDetailSource).toBeDefined();
    expect(actDetailSource).not.toMatch(/\bwindow\.prompt\s*\(/);
  });
});
