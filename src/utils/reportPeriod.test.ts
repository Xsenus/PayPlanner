// @vitest-environment node

import { describe, expect, it } from 'vitest';
import { buildReportMonthPresetRange, currentReportMonthInTimeZone, shiftReportMonth } from './reportPeriod';

describe('reportPeriod', () => {
  it('builds the default reports month from the profile time zone', () => {
    const instant = new Date('2026-06-30T21:30:00.000Z');

    expect(currentReportMonthInTimeZone('Europe/Samara', instant)).toBe('2026-07');
    expect(currentReportMonthInTimeZone('America/New_York', instant)).toBe('2026-06');
  });

  it('shifts report months without using the browser time zone', () => {
    expect(shiftReportMonth('2026-01', -1)).toBe('2025-12');
    expect(shiftReportMonth('2026-12', 1)).toBe('2027-01');
    expect(shiftReportMonth('2026-03', -2)).toBe('2026-01');
  });

  it('builds quick month presets from the profile time zone month', () => {
    const instant = new Date('2026-06-30T21:30:00.000Z');

    expect(buildReportMonthPresetRange('prev', 'Europe/Samara', instant)).toEqual({
      from: '2026-06',
      to: '2026-06',
    });
    expect(buildReportMonthPresetRange('q', 'Europe/Samara', instant)).toEqual({
      from: '2026-05',
      to: '2026-07',
    });
    expect(buildReportMonthPresetRange('y', 'Europe/Samara', instant)).toEqual({
      from: '2026-01',
      to: '2026-12',
    });

    expect(buildReportMonthPresetRange('prev', 'America/New_York', instant)).toEqual({
      from: '2026-05',
      to: '2026-05',
    });
    expect(buildReportMonthPresetRange('q', 'America/New_York', instant)).toEqual({
      from: '2026-04',
      to: '2026-06',
    });
  });
});
