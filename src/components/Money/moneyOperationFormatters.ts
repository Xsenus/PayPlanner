import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';

export function formatMoneyOperationDate(value: string, timeZoneId?: string | null) {
  if (!value) return '—';

  const trimmed = value.trim();
  const isoDateMatch = trimmed.match(/^(\d{4})-(\d{2})-(\d{2})$/);
  if (isoDateMatch) {
    return `${isoDateMatch[3]}.${isoDateMatch[2]}.${isoDateMatch[1]}`;
  }

  if (/^\d{4}-\d{2}-\d{2}T/.test(trimmed)) {
    return formatDateTimeInTimeZone(trimmed, timeZoneId) || value;
  }

  const dotIsoDateMatch = trimmed.match(/^(\d{4})\.(\d{2})\.(\d{2})$/);
  if (dotIsoDateMatch) {
    return `${dotIsoDateMatch[3]}.${dotIsoDateMatch[2]}.${dotIsoDateMatch[1]}`;
  }

  const malformedDateMatch = trimmed.match(/^(\d{2})T\d{2}:\d{2}:\d{2}\.(\d{2})\.(\d{4})$/);
  if (malformedDateMatch) {
    return `${malformedDateMatch[1]}.${malformedDateMatch[2]}.${malformedDateMatch[3]}`;
  }

  const ruDateMatch = trimmed.match(/^(\d{2})\.(\d{2})\.(\d{4})$/);
  if (ruDateMatch) {
    return `${ruDateMatch[1]}.${ruDateMatch[2]}.${ruDateMatch[3]}`;
  }

  const parsedDate = new Date(trimmed);
  if (!Number.isNaN(parsedDate.getTime())) {
    return formatDateTimeInTimeZone(parsedDate, timeZoneId) || parsedDate.toLocaleDateString('ru-RU');
  }

  return value;
}
