import { fromTimeZoneDateTimeInput, toTimeZoneDateTimeInput } from './timeZoneDate';
import type { ClientCase } from '../types';

export function captureCaseEditSource(source: ClientCase): ClientCase {
  return Object.fromEntries(Object.entries(source).filter(([, value]) => value === null || ['string', 'number', 'boolean'].includes(typeof value))) as unknown as ClientCase;
}

export function caseWritSubmissionInput(date: string, destination: string, original: Partial<ClientCase>) {
  const clear = !date && Boolean(original.executiveWritSubmittedAt);
  const selected = destination === (original.executiveWritSubmittedTo ?? '') ? original.executiveWritSubmittedTo ?? null : destination;
  return {
    executiveWritSubmittedAt: caseDateInput(date, original.executiveWritSubmittedAt),
    executiveWritSubmittedTo: (date ? destination : clear ? '' : selected) as ClientCase['executiveWritSubmittedTo'],
    clearExecutiveWritSubmission: clear,
  };
}

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
