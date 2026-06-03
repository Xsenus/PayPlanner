import { describe, expect, it } from 'vitest';
import {
  formatDateTimeInTimeZone,
  fromTimeZoneDateTimeInput,
  toTimeZoneDateTimeInput,
} from './timeZoneDate';

describe('timeZoneDate', () => {
  it('formats UTC instants in the selected user time zone', () => {
    expect(toTimeZoneDateTimeInput('2026-06-05T12:00:00Z', 'Europe/Samara')).toBe('2026-06-05T16:00');
    expect(toTimeZoneDateTimeInput('2026-06-05T12:00:00Z', 'Asia/Novosibirsk')).toBe('2026-06-05T19:00');
  });

  it('converts datetime-local values from the selected user time zone to UTC', () => {
    expect(fromTimeZoneDateTimeInput('2026-06-05T16:00', 'Europe/Samara')).toBe('2026-06-05T12:00:00.000Z');
    expect(fromTimeZoneDateTimeInput('2026-06-05T19:00', 'Asia/Novosibirsk')).toBe('2026-06-05T12:00:00.000Z');
  });

  it('uses the selected time zone for display text', () => {
    expect(formatDateTimeInTimeZone('2026-06-05T12:00:00Z', 'Europe/Samara')).toContain('16:00');
  });
});
