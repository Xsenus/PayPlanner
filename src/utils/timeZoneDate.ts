const DATE_TIME_PARTS = ['year', 'month', 'day', 'hour', 'minute', 'second'] as const;

type DateTimePart = (typeof DATE_TIME_PARTS)[number];

type ZonedParts = Record<DateTimePart, number>;

const NAIVE_ISO_DATE_TIME_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?$/;

export function parseInstant(value: string | Date): Date {
  if (value instanceof Date) return value;

  // PostgreSQL `timestamp without time zone` values contain UTC clock time,
  // but ASP.NET serializes them without a suffix. JavaScript otherwise treats
  // such strings as browser-local time and applies the user's offset twice.
  return new Date(NAIVE_ISO_DATE_TIME_PATTERN.test(value) ? `${value}Z` : value);
}

function safeTimeZone(timeZoneId?: string | null) {
  const value = (timeZoneId || '').trim();
  return value || Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
}

function getFormatter(timeZoneId?: string | null) {
  return new Intl.DateTimeFormat('en-US', {
    timeZone: safeTimeZone(timeZoneId),
    hourCycle: 'h23',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
}

export function getZonedParts(value: string | Date, timeZoneId?: string | null): ZonedParts | null {
  const date = parseInstant(value);
  if (Number.isNaN(date.getTime())) return null;

  const parts = getFormatter(timeZoneId).formatToParts(date);
  const result = Object.fromEntries(DATE_TIME_PARTS.map((part) => [part, 0])) as ZonedParts;
  for (const part of parts) {
    if ((DATE_TIME_PARTS as readonly string[]).includes(part.type)) {
      result[part.type as DateTimePart] = Number(part.value);
    }
  }
  return result;
}

export function toTimeZoneDateTimeInput(value: string | Date | null | undefined, timeZoneId?: string | null) {
  if (!value) return '';
  const parts = getZonedParts(value, timeZoneId);
  if (!parts) return '';
  return [
    String(parts.year).padStart(4, '0'),
    String(parts.month).padStart(2, '0'),
    String(parts.day).padStart(2, '0'),
  ].join('-') + `T${String(parts.hour).padStart(2, '0')}:${String(parts.minute).padStart(2, '0')}`;
}

export function fromTimeZoneDateTimeInput(value: string, timeZoneId?: string | null) {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(value);
  if (!match) return new Date().toISOString();

  const [, year, month, day, hour, minute] = match;
  const target = {
    year: Number(year),
    month: Number(month),
    day: Number(day),
    hour: Number(hour),
    minute: Number(minute),
  };
  let utcMs = Date.UTC(target.year, target.month - 1, target.day, target.hour, target.minute, 0, 0);
  for (let i = 0; i < 3; i += 1) {
    const offset = getTimeZoneOffsetMs(new Date(utcMs), timeZoneId);
    utcMs = Date.UTC(target.year, target.month - 1, target.day, target.hour, target.minute, 0, 0) - offset;
  }
  return new Date(utcMs).toISOString();
}

export function getEffectiveScheduleTime(
  value: string | null | undefined,
  allDay: boolean | undefined,
  timeZoneId?: string | null,
) {
  if (!value) return null;

  const instant = parseInstant(value);
  const time = instant.getTime();
  if (!Number.isFinite(time) || allDay !== true) return Number.isFinite(time) ? time : null;

  const dateKey = /^(\d{4}-\d{2}-\d{2})/.exec(value)?.[1];
  if (!dateKey) return time;

  const endOfDayMinute = parseInstant(fromTimeZoneDateTimeInput(`${dateKey}T23:59`, timeZoneId)).getTime();
  return Number.isFinite(endOfDayMinute) ? endOfDayMinute + 59_999 : time;
}

export function formatDateTimeInTimeZone(value: string | Date, timeZoneId?: string | null) {
  const date = parseInstant(value);
  if (Number.isNaN(date.getTime())) return '';
  return new Intl.DateTimeFormat('ru-RU', {
    timeZone: safeTimeZone(timeZoneId),
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date);
}

export function formatDateTimeWithSecondsInTimeZone(value: string | Date, timeZoneId?: string | null) {
  const date = parseInstant(value);
  if (Number.isNaN(date.getTime())) return '';
  return new Intl.DateTimeFormat('ru-RU', {
    timeZone: safeTimeZone(timeZoneId),
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  }).format(date);
}

export function formatDateInTimeZone(value: string | Date, timeZoneId?: string | null) {
  const date = parseInstant(value);
  if (Number.isNaN(date.getTime())) return '';
  return new Intl.DateTimeFormat('ru-RU', {
    timeZone: safeTimeZone(timeZoneId),
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
  }).format(date);
}

export function formatDateHeadingInTimeZone(value: string | Date | null | undefined, timeZoneId?: string | null) {
  if (!value) return 'Дата не указана';

  const parts = getZonedParts(value, timeZoneId);
  if (!parts) return 'Дата не указана';

  const formattedParts = new Intl.DateTimeFormat('ru-RU', {
    day: 'numeric',
    month: 'long',
    year: 'numeric',
    timeZone: 'UTC',
  }).formatToParts(new Date(Date.UTC(parts.year, parts.month - 1, parts.day)));
  const month = formattedParts.find((part) => part.type === 'month')?.value;

  return month ? `${parts.day} ${month} ${parts.year} года` : 'Дата не указана';
}

export function toTimeZoneDateKey(value: string | Date, timeZoneId?: string | null) {
  const parts = getZonedParts(value, timeZoneId);
  if (!parts) return '';
  return [
    String(parts.year).padStart(4, '0'),
    String(parts.month).padStart(2, '0'),
    String(parts.day).padStart(2, '0'),
  ].join('-');
}

export function getTimeZoneOffsetMs(date: Date, timeZoneId?: string | null) {
  const parts = getZonedParts(date, timeZoneId);
  if (!parts) return 0;
  const asUtc = Date.UTC(parts.year, parts.month - 1, parts.day, parts.hour, parts.minute, parts.second);
  return asUtc - date.getTime();
}
