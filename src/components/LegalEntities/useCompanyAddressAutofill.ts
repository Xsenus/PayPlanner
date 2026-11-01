import { useCallback, useLayoutEffect, useMemo, useRef, useState, type Dispatch, type SetStateAction } from 'react';
import { useDocumentDeliveryOwner, type DocumentDeliveryRead } from '../../hooks/useDocumentDeliveryOwner';
import { apiService } from '../../services/api';
import type { CleanedAddress, DadataAddressSuggestion } from '../../types';
import type { ClientAddressEntryFormValue as Entry } from '../Clients/ClientAddressEntriesForm';
import { chooseBestDisplayAddress } from '../Clients/addressEntryUtils';

const FIELDS = ['fullAddress', 'postalCode', 'country', 'region', 'district', 'city', 'settlement', 'street', 'house', 'flat', 'fiasId', 'kladrId', 'geoLat', 'geoLon'] as const;
const sameAddress = (a: Entry, b: Entry) => FIELDS.every(key => a[key] === b[key]);
type Request = { key: string; snapshot: Entry; owner: DocumentDeliveryRead; controller: AbortController; kind: 'clean' | 'suggest'; pending: boolean };
type Result = { request: Request; items: DadataAddressSuggestion[]; error?: string };
interface Options {
  entries: Entry[]; setEntries: Dispatch<SetStateAction<Entry[]>>; scope: string; enabled: boolean;
  resetKey: unknown; onUseAddress: () => void;
}

function cleanedEntry(entry: Entry, cleaned: CleanedAddress): Entry {
  const updated = { ...entry, postalCode: cleaned.postalCode ?? '', country: cleaned.country ?? entry.country ?? '',
    region: cleaned.regionWithType ?? '', district: cleaned.areaWithType ?? '', city: cleaned.cityWithType ?? '',
    settlement: cleaned.settlementWithType ?? '', street: cleaned.streetWithType ?? '', house: cleaned.house ?? '', flat: cleaned.flat ?? '',
    fiasId: cleaned.fiasId ?? '', kladrId: cleaned.kladrId ?? '', geoLat: cleaned.geoLat ?? '', geoLon: cleaned.geoLon ?? '' };
  return { ...updated, fullAddress: chooseBestDisplayAddress(updated, entry.fullAddress, cleaned.result, entry.fullAddress) };
}

/** Owns only unsaved address UI; no database IDs or document snapshots are changed. */
export function useCompanyAddressAutofill({ entries, setEntries, scope, enabled, resetKey, onUseAddress }: Options) {
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
        if (current(request)) setResults(previous => new Map(previous).set(request.key, { request, items: items ?? [] }));
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
      if (!cleaned || typeof cleaned.result !== 'string') throw new Error('Invalid address response');
      setEntries(previous => owns(request) ? previous.map(entry => entry.uiKey === request.key && sameAddress(entry, request.snapshot) ? cleanedEntry(entry, cleaned) : entry) : previous);
    } catch {
      if (owns(request)) setResults(previous => new Map(previous).set(request.key, { request, items: [], error: 'Не удалось уточнить адрес. Введённые данные сохранены.' }));
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
    const selected = (suggestion.unrestrictedValue ?? suggestion.value ?? '').trim(); if (!selected) return;
    const next = { ...entry, postalCode: suggestion.postalCode ?? '', country: entry.country ?? '', region: suggestion.regionWithType ?? '',
      district: suggestion.areaWithType ?? '', city: suggestion.cityWithType ?? '', settlement: suggestion.settlementWithType ?? '', street: suggestion.streetWithType ?? '',
      house: suggestion.house ?? '', flat: suggestion.flat ?? '', fiasId: suggestion.fiasId ?? entry.fiasId ?? '', kladrId: suggestion.kladrId ?? entry.kladrId ?? '',
      geoLat: suggestion.geoLat ?? entry.geoLat ?? '', geoLon: suggestion.geoLon ?? entry.geoLon ?? '' };
    next.fullAddress = chooseBestDisplayAddress(next, selected, entry.fullAddress);
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
  return { schedule, clean, apply, invalidate, suggestionsByIndex, errorsByIndex };
}
