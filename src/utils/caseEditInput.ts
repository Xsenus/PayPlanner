import { fromTimeZoneDateTimeInput, toTimeZoneDateTimeInput } from './timeZoneDate';

export function caseTextInput(value: string, original: string | null | undefined) {
  const initial = original ?? '';
  const unchanged = value === initial || value.replace(/\r\n?/g, '\n') === initial.replace(/\r\n?/g, '\n');
  return unchanged ? original ?? '' : value.trim();
}

export function caseNullableTextInput(value: string, original: string | null | undefined) {
  return value === (original ?? '') ? original ?? null : value.trim();
}

export function caseDateInput(value: string, original: string | null | undefined) {
  return value === (original ? String(original).slice(0, 10) : '') ? original ?? null : value || null;
}

export function caseHearingInput(value: string, original: string | null | undefined, timeZoneId: string) {
  return value === toTimeZoneDateTimeInput(original, timeZoneId)
    ? original ?? null
    : value ? fromTimeZoneDateTimeInput(value, timeZoneId) : null;
}
