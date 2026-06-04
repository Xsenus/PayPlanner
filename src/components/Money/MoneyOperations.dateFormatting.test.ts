import { describe, expect, it } from 'vitest';

import { formatDate } from './MoneyOperations';

describe('MoneyOperations date formatting', () => {
  it('keeps accounting dates as local calendar dates', () => {
    expect(formatDate('2026-06-04', 'America/New_York')).toBe('04.06.2026');
    expect(formatDate('2026.06.04', 'Europe/Samara')).toBe('04.06.2026');
    expect(formatDate('04.06.2026', 'Asia/Novosibirsk')).toBe('04.06.2026');
  });

  it('formats exact UTC moments in the selected profile time zone', () => {
    expect(formatDate('2026-06-04T22:30:00Z', 'Europe/Samara')).toContain('05.06.2026');
    expect(formatDate('2026-06-04T22:30:00Z', 'Europe/Samara')).toContain('02:30');

    expect(formatDate('2026-06-04T22:30:00Z', 'America/New_York')).toContain('04.06.2026');
    expect(formatDate('2026-06-04T22:30:00Z', 'America/New_York')).toContain('18:30');
  });
});
