import { useState, useEffect, useLayoutEffect, useCallback, useRef, type Dispatch, type SetStateAction } from 'react';
import { apiService } from '../services/api';
import { useDocumentDeliveryOwner } from './useDocumentDeliveryOwner';
import { awaitOwnedUiResult, StaleUiOperationError } from '../utils/StaleUiOperationError';
import { isHttpErrorStatus } from '../utils/errorUtils';
import type { Client, ClientStats, ClientRole, ClientInput } from '../types';

function normalizeClient(client: Client): Client {
  return { ...client, legalEntityId: client.legalEntityId ?? null, legalEntity: client.legalEntity ?? null,
    responsibleUserId: client.responsibleUserId ?? null, responsibleUserName: client.responsibleUserName ?? null,
    clientStatusId: client.clientStatusId ?? null, clientStatus: client.clientStatus ?? null,
    isClientAndSupplier: Boolean(client.isClientAndSupplier),
    cases: Array.isArray(client.cases) ? client.cases : [], contracts: Array.isArray(client.contracts) ? client.contracts : [] };
}
const isClient = (value: Client) => value && Number.isSafeInteger(value.id) && value.id > 0 && typeof value.createdAt === 'string' && typeof value.name === 'string';
const invalidClients = () => new Error('Не удалось подтвердить данные клиентов. Обновите список.');
const denied = (error: unknown) => isHttpErrorStatus(error, 401) || isHttpErrorStatus(error, 403);
// Paged endpoints return summaries. Reuse expanded details only inside the same access owner.
const withDetails = (next: Client, previous?: Client): Client => !previous ? next : { ...next,
  cases: next.cases?.length ? next.cases : previous.cases,
  contracts: next.contracts?.length ? next.contracts : previous.contracts };

export type ClientsListMode = 'all' | 'paged';
export type ClientsListOptions = {
  mode?: ClientsListMode;
  enabled?: boolean;
  ownerScope?: string;
  search?: string;
  isActive?: boolean;
  sortBy?: 'name' | 'createdAt';
  sortDir?: 'asc' | 'desc';
  page?: number;
  pageSize?: number;
  filters?: Record<string, string>;
};
function serializeClientFilters(filters?: Record<string, string>) {
  if (!filters) return '';
  return Object.keys(filters).sort().map(key => `${encodeURIComponent(key)}=${encodeURIComponent(filters[key] ?? '')}`).join('&');
}
function deserializeClientFilters(serialized: string) {
  return Object.fromEntries(serialized.split('&').filter(Boolean).map(part => {
    const [key, value = ''] = part.split('='); return [decodeURIComponent(key), decodeURIComponent(value)];
  }));
}
type ClientsView = { scope: string; rows: Client[]; total: number; loading: boolean; refreshing: boolean; error: string | null };
const emptyClients: Client[] = [];

export function useClients(options?: ClientsListOptions) {
  const enabled = options?.enabled ?? true;
  const { mode, search, isActive, sortBy, sortDir, page, pageSize } = options ?? {};
  const filtersKey = serializeClientFilters(options?.filters);
  const { capture, scope } = useDocumentDeliveryOwner(JSON.stringify([enabled, options?.ownerScope ?? '']));
  const queryOwner = useDocumentDeliveryOwner(JSON.stringify([scope, mode, search, isActive, sortBy, sortDir, page, pageSize, filtersKey]));
  const { capture: captureQuery, scope: queryScope } = queryOwner;
  const [view, setView] = useState<ClientsView>({ scope, rows: [], total: 0, loading: enabled, refreshing: false, error: null });
  const loadedScope = useRef<string | null>(null);
  const read = useRef<{ sequence: number; controller: AbortController | null }>({ sequence: 0, controller: null });
  const mutations = useRef(new Map<number, { current: boolean }>());
  const cancelRead = useCallback(() => { read.current.sequence++; read.current.controller?.abort(); read.current.controller = null; }, []);
  useLayoutEffect(() => {
    cancelRead(); loadedScope.current = null; mutations.current.clear();
    setView({ scope, rows: [], total: 0, loading: enabled, refreshing: false, error: null });
    const pending = mutations.current;
    return () => { cancelRead(); pending.clear(); };
  }, [scope, enabled, cancelRead]);

  const fetchClients = useCallback(async (loadMode?: 'initial' | 'refresh') => {
    const captured = enabled ? captureQuery() : null;
    if (!captured) return;
    cancelRead(); const sequence = read.current.sequence;
    const controller = new AbortController(); read.current.controller = controller;
    const current = () => captured.isCurrent() && sequence === read.current.sequence && !controller.signal.aborted;
    const initial = loadMode ? loadMode === 'initial' : loadedScope.current !== scope;
    setView(previous => ({ scope, rows: previous.scope === scope ? previous.rows : [], total: previous.scope === scope ? previous.total : 0,
      loading: initial, refreshing: !initial, error: null }));
    try {
      const response = mode === 'paged' ? await apiService.getClientsV2({ search, isActive, sortBy, sortDir, page, pageSize,
        ...deserializeClientFilters(filtersKey) }, { signal: controller.signal }) : null;
      const data = response ? response.items : mode === 'paged' ? null : await apiService.getClients({ signal: controller.signal });
      if (!current()) return;
      if (!Array.isArray(data) || !data.every(isClient) || new Set(data.map(row => row.id)).size !== data.length
        || (response && (!Number.isSafeInteger(response.total) || response.total < 0))) throw invalidClients();
      const rows = data.map(normalizeClient);
      loadedScope.current = scope;
      setView(previous => {
        if (!current()) return previous;
        const previousById = new Map((previous.scope === scope ? previous.rows : []).map(row => [row.id, row]));
        return { scope, rows: mode === 'paged' ? rows.map(row => withDetails(row, previousById.get(row.id))) : rows,
          total: response?.total ?? rows.length, loading: false, refreshing: false, error: null };
      });
    } catch (error) {
      if (current()) {
        loadedScope.current = scope;
        setView(previous => current() && previous.scope === scope ? { ...previous,
          rows: denied(error) ? [] : previous.rows, total: denied(error) ? 0 : previous.total,
          loading: false, refreshing: false, error: error instanceof Error ? error.message : 'Failed to fetch clients' } : previous);
      }
    } finally { if (read.current.controller === controller) read.current.controller = null; }
  }, [enabled, captureQuery, scope, cancelRead, mode, search, isActive, sortBy, sortDir, page, pageSize, filtersKey]);
  useEffect(() => { void fetchClients(); return cancelRead; }, [fetchClients, cancelRead]);
  // Writes may finish after a filter change, but can refresh only the latest query of the same authority.
  const latest = useRef({ fetchClients, queryScope, mode }); latest.current = { fetchClients, queryScope, mode };
  const setClients: Dispatch<SetStateAction<Client[]>> = useCallback(next => {
    const captured = enabled ? captureQuery() : null; if (!captured) return;
    setView(previous => captured.isCurrent() && previous.scope === scope ? { ...previous,
      rows: typeof next === 'function' ? next(previous.rows) : next } : previous);
  }, [enabled, captureQuery, scope]);

  const write = useCallback(async (operation: () => Promise<Client | void>, kind: 'create' | 'update' | 'delete', id?: number): Promise<void> => {
    const captured = enabled ? capture() : null; if (!captured) throw new StaleUiOperationError();
    const token = { current: true };
    if (id !== undefined) { const previous = mutations.current.get(id); if (previous) previous.current = false; mutations.current.set(id, token); }
    const current = () => captured.isCurrent() && token.current;
    const sequence = read.current.sequence;
    try {
      const value = await awaitOwnedUiResult(operation, current);
      if (kind !== 'delete' && (!value || !isClient(value) || (id !== undefined && value.id !== id))) throw invalidClients();
      const reload = kind === 'update' || latest.current.mode === 'paged' || sequence !== read.current.sequence
        || read.current.controller !== null || latest.current.queryScope !== queryScope;
      cancelRead();
      const normalized = value ? normalizeClient(value) : null;
      setView(previous => {
        if (!current() || previous.scope !== scope) return previous;
        // Do not insert an unfiltered receipt into another page/search while its new read is pending.
        if (latest.current.queryScope !== queryScope) return { ...previous, loading: false, refreshing: false };
        let rows = previous.rows;
        if (kind === 'delete') rows = rows.filter(row => row.id !== id);
        else if (kind === 'create' && normalized) rows = [normalized, ...rows.filter(row => row.id !== normalized.id)];
        else if (normalized) rows = rows.map(row => row.id === id ? withDetails({ ...row, ...normalized,
          legalEntity: normalized.legalEntity ?? row.legalEntity ?? null, clientStatus: normalized.clientStatus ?? row.clientStatus ?? null }, row) : row);
        return { ...previous, rows, total: previous.total + rows.length - previous.rows.length, loading: false, refreshing: false, error: null };
      });
      if (reload) {
        const refresh = latest.current.fetchClients();
        if (kind !== 'update') { await refresh; if (!current()) throw new StaleUiOperationError(); }
      }
    } catch (error) {
      if (!current()) throw new StaleUiOperationError();
      if (denied(error)) cancelRead();
      setView(previous => current() && previous.scope === scope ? { ...previous, error: error instanceof Error ? error.message : 'Failed to save client',
        rows: denied(error) ? [] : previous.rows, total: denied(error) ? 0 : previous.total,
        loading: denied(error) ? false : previous.loading, refreshing: denied(error) ? false : previous.refreshing } : previous);
      throw error;
    } finally { if (id !== undefined && mutations.current.get(id) === token) mutations.current.delete(id); }
  }, [enabled, capture, scope, queryScope, cancelRead]);
  const createClient = useCallback((payload: ClientInput) => write(() => apiService.createClient(payload), 'create'), [write]);
  const updateClient = useCallback((id: number, patch: ClientInput) => write(() => apiService.updateClient(id, patch), 'update', id), [write]);
  // Remove after acknowledgement: a failed delete does not need an unsafe rollback or extra GET.
  const deleteClient = useCallback((id: number) => write(() => apiService.deleteClient(id), 'delete', id), [write]);
  const visible = view.scope === scope ? view : null;
  return { clients: enabled ? visible?.rows ?? emptyClients : emptyClients, loading: enabled && (visible?.loading ?? true), refreshing: enabled && (visible?.refreshing ?? false),
    error: enabled ? visible?.error ?? null : null, total: enabled ? visible?.total ?? 0 : 0, createClient, updateClient, deleteClient, setClients, refresh: fetchClients };
}

export function useClientStats(clientId: number, caseId?: number, clientRole?: ClientRole, options?: { enabled?: boolean; ownerScope?: string }) {
  const enabled = (options?.enabled ?? true) && Number.isSafeInteger(clientId) && clientId > 0;
  const { capture, scope } = useDocumentDeliveryOwner(JSON.stringify([enabled, clientId, caseId, clientRole, options?.ownerScope ?? '']));
  const [view, setView] = useState<{ scope: string; stats: ClientStats | null; loading: boolean; error: string | null }>({ scope, stats: null, loading: enabled, error: null });
  const read = useRef<{ sequence: number; controller: AbortController | null }>({ sequence: 0, controller: null });
  const cancel = useCallback(() => { read.current.sequence++; read.current.controller?.abort(); read.current.controller = null; }, []);
  const refresh = useCallback(async () => {
    const captured = enabled ? capture() : null; if (!captured) return;
    cancel(); const sequence = read.current.sequence; const controller = new AbortController(); read.current.controller = controller;
    const current = () => captured.isCurrent() && sequence === read.current.sequence && !controller.signal.aborted;
    setView(previous => ({ scope, stats: previous.scope === scope ? previous.stats : null, loading: true, error: null }));
    try {
      const data = await apiService.getClientStats(clientId, { caseId, clientRole }, { signal: controller.signal });
      if (!current()) return;
      if (!data || data.clientId !== clientId) throw invalidClients();
      setView(previous => current() ? { scope, stats: data, loading: false, error: null } : previous);
    } catch (error) {
      if (current()) setView(previous => current() ? { scope, stats: denied(error) ? null : previous.scope === scope ? previous.stats : null,
        loading: false, error: error instanceof Error ? error.message : 'Failed to fetch client statistics' } : previous);
    } finally { if (read.current.controller === controller) read.current.controller = null; }
  }, [enabled, capture, scope, cancel, clientId, caseId, clientRole]);
  useEffect(() => { void refresh(); return cancel; }, [refresh, cancel]);
  const visible = view.scope === scope ? view : null;
  return { stats: enabled ? visible?.stats ?? null : null, loading: enabled && (visible?.loading ?? true), error: enabled ? visible?.error ?? null : null, refresh };
}
