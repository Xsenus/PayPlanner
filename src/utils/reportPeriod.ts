import { todayYMDInTimeZone } from './dateUtils';

export function currentReportMonthInTimeZone(
  timeZoneId?: string | null,
  now: Date = new Date(),
): `${number}-${string}` {
  return todayYMDInTimeZone(timeZoneId, now).slice(0, 7) as `${number}-${string}`;
}
