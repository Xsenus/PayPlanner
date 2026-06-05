import { describe, expect, it } from 'vitest';
import { currentReportMonthInTimeZone } from './reportPeriod';

describe('reportPeriod', () => {
  it('builds the default reports month from the profile time zone', () => {
    const instant = new Date('2026-06-30T21:30:00.000Z');

    expect(currentReportMonthInTimeZone('Europe/Samara', instant)).toBe('2026-07');
    expect(currentReportMonthInTimeZone('America/New_York', instant)).toBe('2026-06');
  });
});
