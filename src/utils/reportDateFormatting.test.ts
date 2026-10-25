// @vitest-environment node

import { describe, expect, it } from 'vitest';
import { formatReportDate, formatReportDeadlineDate } from './reportDateFormatting';

describe('reportDateFormatting', () => {
  it('keeps date-only values as calendar dates without browser timezone conversion', () => {
    expect(formatReportDate('2026-06-05')).toContain('05');
    expect(formatReportDeadlineDate('2026-06-05', true, 'Europe/Samara')).toContain('05');
  });

  it('formats UTC instants by the selected profile timezone for report dates', () => {
    expect(formatReportDate('2026-06-04T22:30:00Z', 'Europe/Samara')).toContain('05');
    expect(formatReportDate('2026-06-04T22:30:00Z', 'America/New_York')).toContain('04');
  });

  it('uses full date-time formatting for timed deadlines', () => {
    expect(formatReportDeadlineDate('2026-06-04T22:30:00Z', false, 'Europe/Samara')).toContain('02:30');
  });

  it('returns an empty string for invalid report dates', () => {
    expect(formatReportDate('not-a-date', 'Europe/Samara')).toBe('');
    expect(formatReportDate('2026-99-99', 'Europe/Samara')).toBe('');
  });
});
