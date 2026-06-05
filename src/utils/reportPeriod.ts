import { todayYMDInTimeZone } from './dateUtils';

export type ReportMonthKey = `${number}-${string}`;
export type ReportMonthPreset = 'this' | 'prev' | 'q' | 'y';

export function currentReportMonthInTimeZone(
  timeZoneId?: string | null,
  now: Date = new Date(),
): ReportMonthKey {
  return todayYMDInTimeZone(timeZoneId, now).slice(0, 7) as ReportMonthKey;
}

export function shiftReportMonth(monthKey: ReportMonthKey, monthOffset: number): ReportMonthKey {
  const [yearPart, monthPart] = monthKey.split('-').map(Number);
  const zeroBasedMonth = (monthPart || 1) - 1 + monthOffset;
  const targetYear = yearPart + Math.floor(zeroBasedMonth / 12);
  const targetMonth = ((zeroBasedMonth % 12) + 12) % 12;
  return `${targetYear}-${String(targetMonth + 1).padStart(2, '0')}` as ReportMonthKey;
}

export function buildReportMonthPresetRange(
  preset: ReportMonthPreset,
  timeZoneId?: string | null,
  now: Date = new Date(),
): { from: ReportMonthKey; to: ReportMonthKey } {
  const current = currentReportMonthInTimeZone(timeZoneId, now);

  if (preset === 'this') {
    return { from: current, to: current };
  }

  if (preset === 'prev') {
    const previous = shiftReportMonth(current, -1);
    return { from: previous, to: previous };
  }

  if (preset === 'q') {
    return { from: shiftReportMonth(current, -2), to: current };
  }

  const [yearPart] = current.split('-');
  return {
    from: `${yearPart}-01` as ReportMonthKey,
    to: `${yearPart}-12` as ReportMonthKey,
  };
}
