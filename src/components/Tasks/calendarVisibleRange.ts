import { fromTimeZoneDateTimeInput } from '../../utils/timeZoneDate';

type CalendarRangeView = 'day' | 'week' | 'month' | 'all' | 'timeline';

function dayKey(date: Date): string {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

/** The dates match the visible calendar grid, including adjacent-month days. */
export function calendarVisibleRange(anchor: Date, view: CalendarRangeView, timeZoneId: string) {
  if (Number.isNaN(anchor.getTime())) throw new RangeError('Invalid calendar anchor');
  const start = new Date(anchor.getFullYear(), anchor.getMonth(), view === 'month' || view === 'all' ? 1 : anchor.getDate());
  if (view !== 'day') start.setDate(start.getDate() - ((start.getDay() + 6) % 7));
  const days = view === 'day' ? 1 : view === 'week' || view === 'timeline' ? 7 : 42;
  const exclusiveEnd = new Date(start.getFullYear(), start.getMonth(), start.getDate() + days);
  const from = fromTimeZoneDateTimeInput(`${dayKey(start)}T00:00`, timeZoneId);
  const endExclusive = fromTimeZoneDateTimeInput(`${dayKey(exclusiveEnd)}T00:00`, timeZoneId);
  return { from, to: new Date(Date.parse(endExclusive) - 1).toISOString() };
}
