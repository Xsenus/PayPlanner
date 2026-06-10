import { describe, expect, it } from 'vitest';

const positiveTabIndexPattern = /tabIndex\s*=\s*(?:\{\s*[1-9]\d*\s*\}|["'][1-9]\d*["'])/;
const sourceModules = import.meta.glob(['./components/**/*.{ts,tsx}', './hooks/**/*.{ts,tsx}'], {
  eager: true,
  import: 'default',
  query: '?raw',
}) as Record<string, string>;

describe('accessibility source contracts', () => {
  it('does not use positive tabIndex values in application components', () => {
    const offenders = Object.entries(sourceModules)
      .filter(([filePath]) => !/\.test\.(tsx|ts)$/.test(filePath))
      .filter(([, source]) => positiveTabIndexPattern.test(source))
      .map(([filePath]) => filePath);

    expect(offenders).toEqual([]);
  });
});
