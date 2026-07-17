// @vitest-environment node

import { describe, expect, it } from 'vitest';
import { formatPeriodLabel, normalizeStoredPeriod, resolvePreset, resolvePeriod, type PeriodFilter } from './periodUtils';

describe('periodUtils', () => {
  it('resolves current week with monday start', () => {
    const now = new Date(2026, 3, 9);
    const range = resolvePreset('current_week', now);
    expect(range.from).toBe('2026-04-06');
    expect(range.to).toBe('2026-04-12');
  });

  it('resolves previous month correctly', () => {
    const now = new Date(2026, 3, 10);
    const range = resolvePreset('previous_month', now);
    expect(range.from).toBe('2026-03-01');
    expect(range.to).toBe('2026-03-31');
  });

  it('formats label for custom ranges', () => {
    const period: PeriodFilter = { mode: 'custom', from: '2026-04-01', to: '2026-04-10' };
    expect(formatPeriodLabel(period, new Date(2026, 3, 10))).toBe('01.04.2026 — 10.04.2026');
  });

  it('normalizes legacy stored period objects', () => {
    const fallback: PeriodFilter = { mode: 'preset', preset: 'year_to_date' };
    const normalized = normalizeStoredPeriod({ from: '2026-01-01', to: '2026-04-10' }, fallback);
    expect(normalized).toEqual({ mode: 'custom', from: '2026-01-01', to: '2026-04-10' });
  });

  it('resolves preset based ranges', () => {
    const period: PeriodFilter = { mode: 'preset', preset: 'today' };
    const range = resolvePeriod(period, new Date(2026, 3, 10));
    expect(range.from).toBe('2026-04-10');
    expect(range.to).toBe('2026-04-10');
  });
});
