import { describe, expect, it } from 'vitest';
import { calendarVisibleRange } from './calendarVisibleRange';

describe('calendarVisibleRange', () => {
  it('includes the entire visible 42-day month grid and adjacent months', () => {
    const anchor = new Date(2026, 9, 15, 12);
    expect(calendarVisibleRange(anchor, 'month', 'Asia/Novosibirsk')).toEqual({
      from: '2026-09-27T17:00:00.000Z', to: '2026-11-08T16:59:59.999Z',
    });
    expect(calendarVisibleRange(anchor, 'all', 'Asia/Novosibirsk')).toEqual(calendarVisibleRange(anchor, 'month', 'Asia/Novosibirsk'));
  });

  it('uses calendar week boundaries in the selected employee time zone across DST', () => {
    const range = calendarVisibleRange(new Date(2026, 2, 8, 12), 'timeline', 'America/New_York');
    expect(range).toEqual({ from: '2026-03-02T05:00:00.000Z', to: '2026-03-09T03:59:59.999Z' });
    expect(calendarVisibleRange(new Date(2026, 2, 8, 12), 'week', 'America/New_York')).toEqual(range);
  });

  it('keeps the full day through a DST transition and rejects invalid anchors', () => {
    expect(calendarVisibleRange(new Date(2026, 2, 8, 12), 'day', 'America/New_York')).toEqual({
      from: '2026-03-08T05:00:00.000Z', to: '2026-03-09T03:59:59.999Z',
    });
    expect(calendarVisibleRange(new Date(2026, 10, 1, 12), 'day', 'America/New_York')).toEqual({
      from: '2026-11-01T04:00:00.000Z', to: '2026-11-02T04:59:59.999Z',
    });
    expect(() => calendarVisibleRange(new Date(NaN), 'day', 'UTC')).toThrow(RangeError);
  });
});
