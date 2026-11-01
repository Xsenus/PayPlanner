import { useCallback, useLayoutEffect, useMemo, useRef, useState, type Dispatch, type SetStateAction } from 'react';
import { useDocumentDeliveryOwner, type DocumentDeliveryRead } from '../../hooks/useDocumentDeliveryOwner';
import { apiService } from '../../services/api';
import type { CleanedAddress, DadataAddressSuggestion } from '../../types';
import type { ClientAddressEntryFormValue } from '../Clients/ClientAddressEntriesForm';
import { chooseBestDisplayAddress } from '../Clients/addressEntryUtils';
import { isHttpErrorStatus } from '../../utils/errorUtils';

type Entry = ClientAddressEntryFormValue & { sourceQuery?: string };

const FIELDS = ['fullAddress', 'postalCode', 'country', 'region', 'district', 'city', 'settlement', 'street', 'house', 'flat', 'fiasId', 'kladrId', 'geoLat', 'geoLon'] as const;
const sameAddress = (a: Entry, b: Entry) => FIELDS.every(key => a[key] === b[key]);
type Request = { key: string; snapshot: Entry; owner: DocumentDeliveryRead; controller: AbortController; kind: 'clean' | 'suggest'; pending: boolean };
type Result = { request: Request; items: DadataAddressSuggestion[]; error?: string };
interface Options<T extends Entry> {
  entries: T[]; setEntries: Dispatch<SetStateAction<T[]>>; scope: string; enabled: boolean;
  resetKey: unknown; onUseAddress: () => void;
  fallbackToSuggestions?: boolean; preserveMissingSuggestionFields?: boolean;
}

const RESPONSE_FIELDS = ['unrestrictedValue', 'postalCode', 'country', 'regionWithType', 'areaWithType', 'cityWithType', 'settlementWithType', 'streetWithType', 'house', 'flat', 'fiasId', 'kladrId', 'geoLat', 'geoLon'];
function validAddressResponse(value: unknown, label: 'value' | 'result'): boolean {
  if (!value || typeof value !== 'object') return false;
  const row = value as Record<string, unknown>;
  return typeof row[label] === 'string' && Boolean(row[label].trim()) && RESPONSE_FIELDS.every(key => row[key] == null || typeof row[key] === 'string');
}
function assertSuggestions(value: unknown, limit: number): asserts value is DadataAddressSuggestion[] {
  if (!Array.isArray(value) || value.length > limit || !value.every(item => validAddressResponse(item, 'value'))
    || new Set(value.map(item => JSON.stringify([item.value, item.unrestrictedValue ?? '']))).size !== value.length) throw new Error('Invalid suggestions');
}
function cleanedEntry<T extends Entry>(entry: T, cleaned: CleanedAddress, source: string, keepCountry: boolean): T {
  const updated = { ...entry, ...(entry.sourceQuery !== undefined ? { sourceQuery: source } : {}), postalCode: cleaned.postalCode ?? '', country: keepCountry ? cleaned.country || entry.country || '' : cleaned.country ?? entry.country ?? '',
    region: cleaned.regionWithType ?? '', district: cleaned.areaWithType ?? '', city: cleaned.cityWithType ?? '',
    settlement: cleaned.settlementWithType ?? '', street: cleaned.streetWithType ?? '', house: cleaned.house ?? '', flat: cleaned.flat ?? '',
    fiasId: cleaned.fiasId ?? '', kladrId: cleaned.kladrId ?? '', geoLat: cleaned.geoLat ?? '', geoLon: cleaned.geoLon ?? '' };
  return { ...updated, fullAddress: chooseBestDisplayAddress(updated, entry.fullAddress, cleaned.result, entry.fullAddress) };
}

function suggestedEntry<T extends Entry>(entry: T, suggestion: DadataAddressSuggestion, keep: boolean, source: string): T {
  const part = (value: string | null | undefined, previous: string | undefined) => keep ? value || previous || '' : value ?? '';
  const identifier = (value: string | null | undefined, previous: string | undefined) => keep ? value || previous || '' : value ?? previous ?? '';
  const updated = { ...entry, ...(entry.sourceQuery !== undefined ? { sourceQuery: source } : {}),
    postalCode: part(suggestion.postalCode, entry.postalCode), country: entry.country ?? '', region: part(suggestion.regionWithType, entry.region),
    district: part(suggestion.areaWithType, entry.district), city: part(suggestion.cityWithType, entry.city), settlement: part(suggestion.settlementWithType, entry.settlement),
    street: part(suggestion.streetWithType, entry.street), house: part(suggestion.house, entry.house), flat: part(suggestion.flat, entry.flat),
    fiasId: identifier(suggestion.fiasId, entry.fiasId), kladrId: identifier(suggestion.kladrId, entry.kladrId), geoLat: identifier(suggestion.geoLat, entry.geoLat), geoLon: identifier(suggestion.geoLon, entry.geoLon) };
  return { ...updated, fullAddress: chooseBestDisplayAddress(updated, suggestion.unrestrictedValue || suggestion.value, entry.fullAddress, source) };
}

/** Owns only unsaved address UI; no database IDs or document snapshots are changed. */
export function useCompanyAddressAutofill<T extends Entry>({ entries, setEntries, scope, enabled, resetKey, onUseAddress, fallbackToSuggestions = false, preserveMissingSuggestionFields = false }: Options<T>) {
  const owner = useDocumentDeliveryOwner(JSON.stringify([scope, enabled]));
  const rows = useMemo(() => new Map(entries.filter(x => x.uiKey).map(x => [x.uiKey!, x])), [entries]);
  const rowsRef = useRef(rows); rowsRef.current = rows;
  const useAddress = useRef(onUseAddress); useAddress.current = onUseAddress;
  const requests = useRef(new Map<string, Request>());
  const timers = useRef(new Map<string, ReturnType<typeof setTimeout>>());
  const [results, setResults] = useState(new Map<string, Result>());
  const owns = (request: Request) => request.owner.isCurrent() && !request.controller.signal.aborted && requests.current.get(request.key) === request;
  const current = (request: Request) => owns(request) && Boolean(rowsRef.current.get(request.key) && sameAddress(rowsRef.current.get(request.key)!, request.snapshot));
  const clear = useCallback((key: string) => {
    requests.current.get(key)?.controller.abort(); requests.current.delete(key);
    clearTimeout(timers.current.get(key)); timers.current.delete(key);
  }, []);
  const invalidate = (key?: string) => {
    if (!key) return;
    clear(key);
    setResults(previous => { if (!previous.has(key)) return previous; const next = new Map(previous); next.delete(key); return next; });
  };
  useLayoutEffect(() => {
    const active = requests.current; const scheduled = timers.current;
    const cancel = () => { for (const request of active.values()) request.controller.abort(); active.clear(); for (const timer of scheduled.values()) clearTimeout(timer); scheduled.clear(); };
    cancel(); setResults(new Map()); return cancel;
  }, [owner.scope, resetKey]);
  useLayoutEffect(() => {
    for (const [key, request] of requests.current) if (!rows.has(key) || !sameAddress(rows.get(key)!, request.snapshot)) clear(key);
    setResults(previous => {
      if ([...previous.keys()].every(key => rows.has(key))) return previous;
      return new Map([...previous].filter(([key]) => rows.has(key)));
    });
  }, [rows, clear]);
  const start = (key: string | undefined, snapshot?: Entry, kind: Request['kind'] = 'clean') => {
    if (!enabled || !key || !rowsRef.current.has(key)) return null;
    const captured = owner.capture(); if (!captured) return null;
    invalidate(key);
    const request: Request = { key, snapshot: snapshot ?? rowsRef.current.get(key)!, owner: captured, controller: new AbortController(), kind, pending: true };
    requests.current.set(key, request); return request;
  };
  const schedule = (key: string | undefined, query: string) => {
    const entry = key ? rowsRef.current.get(key) : undefined;
    if (!entry) return;
    const request = start(key, { ...entry, fullAddress: query }, 'suggest'); if (!request) return;
    const normalized = query.trim();
    if (normalized.length < 3) { invalidate(key); return; }
    timers.current.set(request.key, setTimeout(async () => {
      timers.current.delete(request.key); if (!current(request)) return;
      try {
        const items = await apiService.suggestClientAddresses(normalized, 7, request.controller.signal);
        if (!current(request)) return;
        assertSuggestions(items, 7);
        setResults(previous => new Map(previous).set(request.key, { request, items }));
      } catch {
        if (current(request)) setResults(previous => new Map(previous).set(request.key, { request, items: [], error: 'Не удалось получить подсказки адреса. Введённые данные сохранены.' }));
      } finally { request.pending = false; }
    }, 350));
  };
  const normalize = async (request: Request, source: string) => {
    if (!owns(request)) return;
    try {
      const cleaned = await apiService.cleanAddress(source, request.controller.signal);
      if (!owns(request)) return;
      if (!validAddressResponse(cleaned, 'result')) throw new Error('Invalid address response');
      setEntries(previous => owns(request) ? previous.map(entry => entry.uiKey === request.key && sameAddress(entry, request.snapshot) ? cleanedEntry(entry, cleaned, source, preserveMissingSuggestionFields) : entry) : previous);
    } catch (error) {
      if (!owns(request)) return;
      const denied = isHttpErrorStatus(error, 401) || isHttpErrorStatus(error, 403);
      // The clean endpoint has already tried its fallback before returning 404.
      if (fallbackToSuggestions && !denied && !isHttpErrorStatus(error, 404)) {
        try {
          const suggestions = await apiService.suggestClientAddresses(source, 1, request.controller.signal);
          if (!owns(request)) return;
          assertSuggestions(suggestions, 1);
          const suggestion = suggestions[0];
          if (suggestion) {
            setEntries(previous => owns(request) ? previous.map(entry => entry.uiKey === request.key && sameAddress(entry, request.snapshot) ? suggestedEntry(entry, suggestion, preserveMissingSuggestionFields, source) : entry) : previous);
            return;
          }
        } catch { /* Preserve the current row and show one owned error below. */ }
      }
      if (owns(request)) setResults(previous => new Map(previous).set(request.key, { request, items: [], error: denied ? 'Недостаточно прав для уточнения адреса. Введённые данные сохранены.' : 'Не удалось уточнить адрес. Введённые данные сохранены.' }));
    } finally { request.pending = false; }
  };
  const clean = async (key?: string) => {
    const entry = key ? rowsRef.current.get(key) : undefined; if (!entry?.fullAddress.trim()) return;
    const active = key ? requests.current.get(key) : undefined;
    if (active?.kind === 'clean' && active.pending && current(active)) return;
    const request = start(key); if (!request) return;
    useAddress.current(); await normalize(request, entry.fullAddress.trim());
  };
  const apply = async (key: string | undefined, suggestion: DadataAddressSuggestion) => {
    const entry = key ? rowsRef.current.get(key) : undefined; const result = key ? results.get(key) : undefined;
    if (!entry || !result || !current(result.request) || !result.items.includes(suggestion)) return;
    const selected = (suggestion.unrestrictedValue || suggestion.value || '').trim(); if (!selected) return;
    const next = suggestedEntry(entry, suggestion, preserveMissingSuggestionFields, selected);
    const request = start(key, next); if (!request) return;
    useAddress.current();
    setEntries(previous => owns(request) ? previous.map(row => row.uiKey === request.key && sameAddress(row, entry) ? { ...next, type: row.type, isPrimary: row.isPrimary, additionalTypes: row.additionalTypes } : row) : previous);
    await normalize(request, selected);
  };
  const suggestionsByIndex: Record<number, DadataAddressSuggestion[]> = {}; const errorsByIndex: Record<number, string> = {};
  if (enabled) entries.forEach((entry, index) => {
    const result = entry.uiKey ? results.get(entry.uiKey) : undefined;
    if (!result || !current(result.request)) return;
    suggestionsByIndex[index] = result.items; if (result.error) errorsByIndex[index] = result.error;
  });
  return { schedule, clean, apply, invalidate, suggestionsByIndex, errorsByIndex, capture: () => enabled ? owner.capture() : null };
}
