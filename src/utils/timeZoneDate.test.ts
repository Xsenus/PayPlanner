// @vitest-environment node

import { describe, expect, it } from 'vitest';
import {
  formatDateInTimeZone,
  formatDateTimeInTimeZone,
  formatDateTimeWithSecondsInTimeZone,
  fromTimeZoneDateTimeInput,
  toTimeZoneDateKey,
  toTimeZoneDateTimeInput,
} from './timeZoneDate';

describe('timeZoneDate', () => {
  it('formats UTC instants in the selected user time zone', () => {
    expect(toTimeZoneDateTimeInput('2026-06-05T12:00:00Z', 'Europe/Moscow')).toBe('2026-06-05T15:00');
    expect(toTimeZoneDateTimeInput('2026-06-05T12:00:00Z', 'Europe/Samara')).toBe('2026-06-05T16:00');
    expect(toTimeZoneDateTimeInput('2026-06-05T12:00:00Z', 'Asia/Novosibirsk')).toBe('2026-06-05T19:00');
  });

  it('treats backend timestamps without a suffix as UTC instants', () => {
    expect(toTimeZoneDateTimeInput('2026-06-05T12:00:00', 'Europe/Moscow')).toBe('2026-06-05T15:00');
    expect(formatDateTimeInTimeZone('2026-06-05T12:00:00', 'Asia/Novosibirsk')).toContain('19:00');
  });

  it('converts datetime-local values from the selected user time zone to UTC', () => {
    expect(fromTimeZoneDateTimeInput('2026-06-05T15:00', 'Europe/Moscow')).toBe('2026-06-05T12:00:00.000Z');
    expect(fromTimeZoneDateTimeInput('2026-06-05T16:00', 'Europe/Samara')).toBe('2026-06-05T12:00:00.000Z');
    expect(fromTimeZoneDateTimeInput('2026-06-05T19:00', 'Asia/Novosibirsk')).toBe('2026-06-05T12:00:00.000Z');
  });

  it('builds calendar day keys in the selected user time zone around midnight', () => {
    const instant = '2026-06-04T20:30:00Z';

    expect(toTimeZoneDateKey(instant, 'Europe/Moscow')).toBe('2026-06-04');
    expect(toTimeZoneDateKey(instant, 'Europe/Samara')).toBe('2026-06-05');
    expect(toTimeZoneDateKey(instant, 'Asia/Novosibirsk')).toBe('2026-06-05');
  });

  it('uses the selected time zone for display text', () => {
    expect(formatDateTimeInTimeZone('2026-06-05T12:00:00Z', 'Europe/Samara')).toContain('16:00');
  });

  it('keeps seconds in detailed journal display text', () => {
    expect(formatDateTimeWithSecondsInTimeZone('2026-06-05T12:00:07Z', 'Europe/Samara')).toContain('16:00:07');
  });

  it('formats date-only display in the selected user time zone', () => {
    expect(formatDateInTimeZone('2026-06-04T22:30:00Z', 'Europe/Samara')).toBe('05.06.2026');
    expect(formatDateInTimeZone('2026-06-04T22:30:00Z', 'Europe/Moscow')).toBe('05.06.2026');
  });
});
