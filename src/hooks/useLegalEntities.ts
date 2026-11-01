import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import { useDocumentDeliveryOwner } from './useDocumentDeliveryOwner';
import { awaitOwnedUiResult, StaleUiOperationError } from '../utils/StaleUiOperationError';
import { isHttpErrorStatus } from '../utils/errorUtils';
import type { LegalEntityDetail, LegalEntityInput, LegalEntitySuggestion, LegalEntitySummary } from '../types';

type View = { scope: string; rows: LegalEntitySummary[]; loading: boolean; error: string | null };
const validEntity = (value: LegalEntitySummary) => value && Number.isSafeInteger(value.id) && value.id > 0 && typeof value.shortName === 'string';
const invalidData = () => new Error('Не удалось подтвердить данные справочника компаний. Обновите список.');

export function useLegalEntities(options?: { enabled?: boolean; ownerScope?: string }) {
  const enabled = options?.enabled ?? true;
  const owner = useDocumentDeliveryOwner(JSON.stringify([enabled, options?.ownerScope ?? '']));
  const { capture, scope } = owner;
  const [view, setView] = useState<View>({ scope, rows: [], loading: enabled, error: null });
  const read = useRef<{ sequence: number; controller: AbortController | null }>({ sequence: 0, controller: null });
  const query = useRef<string | undefined>(undefined);
  const mutations = useRef(new Map<number, { current: boolean }>());
  const cancelRead = useCallback(() => { read.current.sequence++; read.current.controller?.abort(); read.current.controller = null; }, []);
  useLayoutEffect(() => {
    cancelRead(); query.current = undefined; mutations.current.clear();
    setView({ scope, rows: [], loading: enabled, error: null });
    const pending = mutations.current;
    return () => { cancelRead(); pending.clear(); };
  }, [scope, enabled, cancelRead]);

  const fetchAll = useCallback(async (search?: string) => {
    if (!enabled) return;
    const captured = capture(); if (!captured) return;
    cancelRead(); const sequence = read.current.sequence; const controller = new AbortController(); read.current.controller = controller;
    query.current = search;
    const current = () => captured.isCurrent() && sequence === read.current.sequence && !controller.signal.aborted;
    setView(previous => ({ scope, rows: previous.scope === scope ? previous.rows : [], loading: true, error: null }));
    try {
      const data = await apiService.getLegalEntities(search ? { search } : undefined, { signal: controller.signal });
      if (!current()) return;
      if (!Array.isArray(data) || !data.every(validEntity) || new Set(data.map(row => row.id)).size !== data.length) throw invalidData();
      setView(previous => current() ? { scope, rows: data, loading: false, error: null } : previous);
    } catch (error) {
      if (current()) setView(previous => previous.scope === scope && current() ? { ...previous,
        rows: isHttpErrorStatus(error, 401) || isHttpErrorStatus(error, 403) ? [] : previous.rows,
        loading: false, error: error instanceof Error ? error.message : 'Не удалось загрузить юридические лица' } : previous);
    } finally {
      if (read.current.controller === controller) read.current.controller = null;
    }
  }, [enabled, capture, scope, cancelRead]);
  useEffect(() => { void fetchAll(); }, [fetchAll]);

  const admit = useCallback(() => {
    const captured = enabled ? capture() : null;
    if (!captured) throw new StaleUiOperationError();
    return captured;
  }, [enabled, capture]);
  const write = useCallback(async <T,>(operation: () => Promise<T>, apply: (rows: LegalEntitySummary[], value: T) => LegalEntitySummary[], id?: number, validate?: (value: T) => boolean) => {
    const captured = admit(); const token = { current: true };
    if (id !== undefined) { const previous = mutations.current.get(id); if (previous) previous.current = false; mutations.current.set(id, token); }
    const sequence = read.current.sequence;
    const current = () => captured.isCurrent() && token.current;
    try {
      const value = await awaitOwnedUiResult(operation, current);
      if (validate && !validate(value)) throw invalidData();
      const reload = sequence !== read.current.sequence || read.current.controller !== null || Boolean(query.current?.trim());
      if (reload) { cancelRead(); void fetchAll(query.current); }
      else setView(previous => current() && previous.scope === scope ? { ...previous, rows: apply(previous.rows, value) } : previous);
      return value;
    } finally {
      if (id !== undefined && mutations.current.get(id) === token) mutations.current.delete(id);
    }
  }, [admit, cancelRead, fetchAll, scope]);
  const createLegalEntity = useCallback((payload: LegalEntityInput) => write<LegalEntityDetail>(() => apiService.createLegalEntity(payload),
    (rows, value) => [{ ...value }, ...rows.filter(row => row.id !== value.id)], undefined, validEntity), [write]);
  const updateLegalEntity = useCallback((id: number, payload: LegalEntityInput) => write<LegalEntityDetail>(() => apiService.updateLegalEntity(id, payload),
    (rows, value) => rows.map(row => row.id === id ? { ...value } : row), id, value => validEntity(value) && value.id === id), [write]);
  const deleteLegalEntity = useCallback((id: number) => write<void>(() => apiService.deleteLegalEntity(id), rows => rows.filter(row => row.id !== id), id), [write]);
  const getLegalEntity = useCallback(async (id: number) => {
    const captured = admit(); const value = await awaitOwnedUiResult(() => apiService.getLegalEntity(id), captured.isCurrent);
    if (!validEntity(value) || value.id !== id) throw invalidData(); return value;
  }, [admit]);
  const suggestLegalEntities = useCallback(async (payload: { query?: string; inn?: string; limit?: number }): Promise<LegalEntitySuggestion[]> => {
    const captured = admit(); return awaitOwnedUiResult(() => apiService.suggestLegalEntities(payload), captured.isCurrent);
  }, [admit]);
  const findLegalEntityById = useCallback(async (payload: { query: string; kpp?: string }) => {
    const captured = admit(); return awaitOwnedUiResult(() => apiService.findLegalEntityById(payload), captured.isCurrent);
  }, [admit]);
  const visible = view.scope === scope ? view : null;
  const sorted = useMemo(() => [...(visible?.rows ?? [])].sort((a, b) => a.shortName.localeCompare(b.shortName, undefined, { sensitivity: 'base' })), [visible?.rows]);
  return { legalEntities: enabled ? sorted : [], loading: enabled && (visible?.loading ?? true), error: enabled ? visible?.error ?? null : null,
    refresh: fetchAll, createLegalEntity, updateLegalEntity, deleteLegalEntity, getLegalEntity, suggestLegalEntities, findLegalEntityById };
}
