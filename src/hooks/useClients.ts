import { useState, useEffect, useCallback, useRef } from 'react';
import { apiService } from '../services/api';
import type {
  Client,
  ClientStats,
  ClientCase,
  ClientRole,
  LegalEntitySummary,
  ClientInput,
  ClientStatus,
} from '../types';

function normalizeClient(client: Client): Client {
  return {
    ...client,
    legalEntityId: client.legalEntityId ?? null,
    legalEntity: (client.legalEntity as LegalEntitySummary | null) ?? null,
    responsibleUserId: client.responsibleUserId ?? null,
    responsibleUserName: client.responsibleUserName ?? null,
    clientStatusId: client.clientStatusId ?? null,
    clientStatus: (client.clientStatus as ClientStatus | null) ?? null,
    isClientAndSupplier: Boolean(client.isClientAndSupplier),
    cases: Array.isArray(client.cases) ? (client.cases as ClientCase[]) : [],
    contracts: Array.isArray(client.contracts) ? client.contracts : [],
  };
}

function isClient(x: unknown): x is Client {
  if (typeof x !== 'object' || x === null) return false;
  const o = x as Record<string, unknown>;
  return typeof o.id === 'number' && typeof o.createdAt === 'string';
}

export type ClientsListMode = 'all' | 'paged';

export type ClientsListOptions = {
  mode?: ClientsListMode;
  enabled?: boolean;
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
  return Object.keys(filters)
    .sort()
    .map((key) => `${encodeURIComponent(key)}=${encodeURIComponent(filters[key] ?? '')}`)
    .join('&');
}

function deserializeClientFilters(serialized: string) {
  if (!serialized) return {};
  return Object.fromEntries(
    serialized
      .split('&')
      .filter(Boolean)
      .map((part) => {
        const [key, value = ''] = part.split('=');
        return [decodeURIComponent(key), decodeURIComponent(value)];
      }),
  );
}

export function useClients(options?: ClientsListOptions) {
  const enabled = options?.enabled ?? true;
  const mode = options?.mode;
  const search = options?.search;
  const isActive = options?.isActive;
  const sortBy = options?.sortBy;
  const sortDir = options?.sortDir;
  const page = options?.page;
  const pageSize = options?.pageSize;
  const filtersKey = serializeClientFilters(options?.filters);
  const [clients, setClients] = useState<Client[]>([]);
  const [loadingInitial, setLoadingInitial] = useState(enabled);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [total, setTotal] = useState(0);
  const hasLoadedRef = useRef(false);
  const requestIdRef = useRef(0);
  const activeRequestControllerRef = useRef<AbortController | null>(null);

  const fetchClients = useCallback(async (loadMode?: 'initial' | 'refresh') => {
    if (!enabled) {
      activeRequestControllerRef.current?.abort();
      activeRequestControllerRef.current = null;
      requestIdRef.current += 1;
      hasLoadedRef.current = true;
      setClients([]);
      setTotal(0);
      setError(null);
      setLoadingInitial(false);
      setRefreshing(false);
      return;
    }

    const nextMode = loadMode ?? (hasLoadedRef.current ? 'refresh' : 'initial');
    const requestId = ++requestIdRef.current;
    activeRequestControllerRef.current?.abort();
    const controller = new AbortController();
    activeRequestControllerRef.current = controller;

    try {
      if (nextMode === 'initial') {
        setLoadingInitial(true);
      } else {
        setRefreshing(true);
      }
      setError(null);

      if (mode === 'paged') {
        const response = await apiService.getClientsV2(
          {
            search,
            isActive,
            sortBy,
            sortDir,
            page,
            pageSize,
            ...deserializeClientFilters(filtersKey),
          },
          { signal: controller.signal },
        );
        if (requestId !== requestIdRef.current) {
          return;
        }
        const normalized = (response.items as Client[]).map((c) => normalizeClient(c));
        setClients((prev) =>
          normalized.map((next) => {
            const current = prev.find((item) => item.id === next.id);
            if (!current) return next;
            return {
              ...next,
              cases:
                (next.cases?.length ?? 0) === 0 && (current.cases?.length ?? 0) > 0
                  ? (current.cases as ClientCase[])
                  : (next.cases ?? []),
              contracts:
                (next.contracts?.length ?? 0) === 0 && (current.contracts?.length ?? 0) > 0
                  ? current.contracts
                  : next.contracts,
            };
          }),
        );
        setTotal(response.total ?? 0);
      } else {
        const base = await apiService.getClients({ signal: controller.signal });
        if (requestId !== requestIdRef.current) {
          return;
        }
        const normalized = (base as Client[]).map((c) => normalizeClient(c));

        setClients(normalized);
        setTotal(normalized.length);
      }
    } catch (err) {
      if (controller.signal.aborted) {
        return;
      }
      if (requestId !== requestIdRef.current) {
        return;
      }
      setError(err instanceof Error ? err.message : 'Failed to fetch clients');
    } finally {
      if (activeRequestControllerRef.current === controller) {
        activeRequestControllerRef.current = null;
      }
      if (requestId === requestIdRef.current) {
        hasLoadedRef.current = true;
        if (nextMode === 'initial') {
          setLoadingInitial(false);
        } else {
          setRefreshing(false);
        }
      }
    }
  }, [enabled, filtersKey, isActive, mode, page, pageSize, search, sortBy, sortDir]);

  useEffect(() => {
    void fetchClients();
    return () => {
      activeRequestControllerRef.current?.abort();
      activeRequestControllerRef.current = null;
      requestIdRef.current += 1;
    };
  }, [fetchClients]);

  const createClient = async (client: ClientInput) => {
    try {
      const created = await apiService.createClient(client);
      if (isClient(created)) {
        const normalized = normalizeClient(created);
        setClients((prev) => [normalized, ...prev]);
        if (mode === 'paged') {
          await fetchClients();
        }
      } else {
        await fetchClients();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create client');
      throw err;
    }
  };

  const updateClient = async (id: number, patch: ClientInput) => {
    try {
      const updated = await apiService.updateClient(id, patch);
      if (isClient(updated)) {
        const normalized = normalizeClient(updated);
        setClients((prev) =>
          prev.map((c) =>
            c.id === id
              ? {
                  ...c,
                  ...normalized,
                  legalEntity:
                    normalized.legalEntity ?? (c.legalEntity as LegalEntitySummary | null) ?? null,
                  clientStatus: normalized.clientStatus ?? (c.clientStatus as ClientStatus | null) ?? null,
                  cases:
                    (normalized.cases?.length ?? 0) === 0 && (c.cases?.length ?? 0) > 0
                      ? (c.cases as ClientCase[])
                      : (normalized.cases ?? []),
                  contracts:
                    (normalized.contracts?.length ?? 0) === 0 && (c.contracts?.length ?? 0) > 0
                      ? c.contracts
                      : normalized.contracts,
                }
              : c,
          ),
        );
        void fetchClients();
      } else {
        await fetchClients();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to update client');
      throw err;
    }
  };

  const deleteClient = async (id: number) => {
    try {
      setClients((prev) => prev.filter((c) => c.id !== id));
      await apiService.deleteClient(id);
      if (mode === 'paged') {
        await fetchClients();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to delete client');
      await fetchClients();
      throw err;
    }
  };

  return {
    clients,
    loading: loadingInitial,
    refreshing,
    error,
    total,
    createClient,
    updateClient,
    deleteClient,
    setClients,
    refresh: fetchClients,
  };
}

export function useClientStats(clientId: number, caseId?: number, clientRole?: ClientRole) {
  const [stats, setStats] = useState<ClientStats | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fetchStats = async () => {
      try {
        setLoading(true);
        const data = await apiService.getClientStats(clientId, {
          caseId,
          clientRole,
        });
        setStats(data);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to fetch client statistics');
      } finally {
        setLoading(false);
      }
    };

    if (clientId) void fetchStats();
  }, [clientId, caseId, clientRole]);

  return {
    stats,
    loading,
    error,
    refresh: async () => {
      const data = await apiService.getClientStats(clientId, {
        caseId,
        clientRole,
      });
      setStats(data);
    },
  };
}
