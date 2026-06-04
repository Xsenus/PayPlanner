import { formatDateTimeInTimeZone, getZonedParts } from './timeZoneDate';

const DATE_ONLY_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;

const shortDateFormatter = new Intl.DateTimeFormat('ru-RU', {
  day: '2-digit',
  month: 'short',
});

function formatLocalDateParts(year: number, month: number, day: number) {
  const date = new Date(year, month - 1, day);
  if (
    Number.isNaN(date.getTime()) ||
    date.getFullYear() !== year ||
    date.getMonth() !== month - 1 ||
    date.getDate() !== day
  ) {
    return '';
  }

  return shortDateFormatter.format(date);
}

export function formatReportDate(value: string, timeZoneId?: string | null) {
  const trimmed = value.trim();
  const dateOnly = DATE_ONLY_PATTERN.exec(trimmed);
  if (dateOnly) {
    const [, year, month, day] = dateOnly;
    return formatLocalDateParts(Number(year), Number(month), Number(day));
  }

  try {
    const parts = getZonedParts(trimmed, timeZoneId);
    if (!parts) return '';
    return formatLocalDateParts(parts.year, parts.month, parts.day);
  } catch {
    return '';
  }
}

export function formatReportDateTime(value: string, timeZoneId?: string | null) {
  try {
    return formatDateTimeInTimeZone(value, timeZoneId);
  } catch {
    return '';
  }
}

export function formatReportDeadlineDate(value: string, allDay?: boolean, timeZoneId?: string | null) {
  return allDay === false ? formatReportDateTime(value, timeZoneId) : formatReportDate(value, timeZoneId);
}
