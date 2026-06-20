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
});
