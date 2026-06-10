import { describe, expect, it } from 'vitest';

import {
  buildTitleSuffix,
  formatCurrency,
  formatDeadlineOffset,
  formatYmLabel,
  monthRangeToDates,
  normalizeYmRange,
} from './reportPresentation';

describe('reportPresentation', () => {
  it('formats report money values with the same smart currency formatter', () => {
    expect(formatCurrency(1200)).toMatch(/1\s*200/);
    expect(formatCurrency(1200)).toContain('₽');
  });

  it('formats year-month labels in Russian', () => {
    expect(formatYmLabel('2026-06')).toBe('июнь 2026 г.');
  });

  it('normalizes reversed month ranges', () => {
    expect(normalizeYmRange('2026-08', '2026-06')).toEqual({
      fromYM: '2026-06',
      toYM: '2026-08',
    });
  });

  it('builds report title suffixes for single and multi-month ranges', () => {
    expect(buildTitleSuffix('2026-06', '2026-06')).toBe('июнь 2026 г.');
    expect(buildTitleSuffix('2026-06', '2026-08')).toBe('июнь 2026 г. - август 2026 г.');
  });

  it('converts month ranges to inclusive date ranges', () => {
    expect(monthRangeToDates('2026-02', '2026-02')).toEqual({
      from: '2026-02-01',
      to: '2026-02-28',
    });
    expect(monthRangeToDates('2024-02', '2024-02')).toEqual({
      from: '2024-02-01',
      to: '2024-02-29',
    });
    expect(monthRangeToDates('2026-08', '2026-06')).toEqual({
      from: '2026-06-01',
      to: '2026-08-31',
    });
  });

  it('formats deadline offsets', () => {
    expect(formatDeadlineOffset(-3)).toBe('3 дн. просрочено');
    expect(formatDeadlineOffset(0)).toBe('сегодня');
    expect(formatDeadlineOffset(5)).toBe('через 5 дн.');
  });
});
