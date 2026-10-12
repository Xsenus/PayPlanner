import { formatDateInTimeZone, getZonedParts } from '../../utils/timeZoneDate';

export function formatClientDate(value?: string | null, timeZoneId?: string | null) {
  if (!value) return '';
  return formatDateInTimeZone(value, timeZoneId) || value;
}

// A birth date is a calendar date, never an instant at midnight UTC.
function parseClientBirthDate(value?: string | null) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value ?? '');
  if (!match) return null;
  const [, year, month, day] = match.map(Number);
  const date = new Date(0);
  date.setUTCFullYear(year, month - 1, day);
  if (year < 1 || date.getUTCFullYear() !== year || date.getUTCMonth() + 1 !== month || date.getUTCDate() !== day) return null;
  return { year, month, day };
}

export function formatClientBirthDate(value?: string | null) {
  const parts = parseClientBirthDate(value);
  if (!parts) return value ?? '';
  return `${String(parts.day).padStart(2, '0')}.${String(parts.month).padStart(2, '0')}.${String(parts.year).padStart(4, '0')}`;
}

export function getClientAgeLabel(birthDate?: string | null, timeZoneId?: string | null) {
  const birth = parseClientBirthDate(birthDate);
  const today = getZonedParts(new Date(), timeZoneId);
  if (!birth || !today) return '';
  let age = today.year - birth.year;
  if (today.month < birth.month || (today.month === birth.month && today.day < birth.day)) age -= 1;
  if (age < 0 || age > 150) return '';
  const lastTwo = age % 100;
  const last = age % 10;
  const word = lastTwo >= 11 && lastTwo <= 14 ? 'лет' : last === 1 ? 'год' : last >= 2 && last <= 4 ? 'года' : 'лет';
  return `${age} ${word}`;
}
