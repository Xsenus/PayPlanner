import { formatDateInTimeZone } from '../../utils/timeZoneDate';

export function formatClientDate(value?: string | null, timeZoneId?: string | null) {
  if (!value) return '';
  return formatDateInTimeZone(value, timeZoneId) || value;
}
