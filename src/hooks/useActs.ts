import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { Act, ActInput, ActStatus, ActsSummary } from '../types';

export type ActsSortKey =
  | 'date'
  | 'number'
  | 'amount'
  | 'invoiceNumber'
  | 'counterpartyInn'
  | 'status'
  | 'client'
  | 'responsible'
  | 'createdAt';

export interface ActsFilters {
  from?: string;
  to?: string;
  status?: ActStatus;
  clientId?: number;
  responsibleId?: number;
  search?: string;
  sortBy?: ActsSortKey;
  sortDir?: 'asc' | 'desc';
  page?: number;
  pageSize?: number;
}

export interface ActsPagination {
  page: number;
  pageSize: number;
  total: number;
}

interface UseActsResult {
  acts: Act[];
  loading: boolean;
  refreshing: boolean;
  error: string | null;
  summary: ActsSummary | null;
  summaryLoading: boolean;
  pagination: ActsPagination;
  refresh: () => Promise<void>;
  createAct: (payload: ActInput) => Promise<Act>;
  updateAct: (id: number, payload: ActInput) => Promise<Act>;
  deleteAct: (id: number) => Promise<void>;
}

function makeSummaryKey(filters: ActsFilters): string {
  return JSON.stringify({
    from: filters.from ?? null,
    to: filters.to ?? null,
    status: filters.status ?? null,
    clientId: filters.clientId ?? null,
    responsibleId: filters.responsibleId ?? null,
    search: filters.search ?? null,
  });
}

export function useActs(filters: ActsFilters): UseActsResult {
  const [acts, setActs] = useState<Act[]>([]);
  const [loading, setLoading] = useState<boolean>(true);
  const [refreshing, setRefreshing] = useState<boolean>(false);
  const [error, setError] = useState<string | null>(null);
  const [summary, setSummary] = useState<ActsSummary | null>(null);
  const [summaryLoading, setSummaryLoading] = useState<boolean>(false);
  const [pagination, setPagination] = useState<ActsPagination>({
    page: filters.page ?? 1,
    pageSize: filters.pageSize ?? 20,
    total: 0,
  });

  const hasLoadedRef = useRef(false);
  const hasSummaryLoadedRef = useRef(false);

  const filtersKey = useMemo(() => JSON.stringify(filters), [filters]);
  const summaryKey = useMemo(() => makeSummaryKey(filters), [filters]);

  const fetchActs = useCallback(
    async (mode: 'auto' | 'refresh' = 'auto') => {
      const isInitial = mode === 'refresh' ? false : !hasLoadedRef.current;
      if (isInitial) {
        setLoading(true);
      } else {
        setRefreshing(true);
      }
      setError(null);
      try {
        const response = await apiService.getActs({
          from: filters.from,
          to: filters.to,
          status: filters.status,
          clientId: filters.clientId,
          responsibleId: filters.responsibleId,
          search: filters.search,
          sortBy: filters.sortBy,
          sortDir: filters.sortDir,
          page: filters.page,
          pageSize: filters.pageSize,
        });
        setActs(response.items ?? []);
        setPagination({
          page: response.page ?? filters.page ?? 1,
          pageSize: response.pageSize ?? filters.pageSize ?? 20,
          total: response.total ?? 0,
        });
        hasLoadedRef.current = true;
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Не удалось загрузить акты';
        setError(message);
        if (!hasLoadedRef.current) {
          setActs([]);
          setPagination({ page: filters.page ?? 1, pageSize: filters.pageSize ?? 20, total: 0 });
        }
      } finally {
        if (isInitial) {
          setLoading(false);
        } else {
          setRefreshing(false);
        }
      }
    },
    [filters.from, filters.to, filters.status, filters.clientId, filters.responsibleId, filters.search, filters.sortBy, filters.sortDir, filters.page, filters.pageSize],
  );

  const fetchSummary = useCallback(
    async (mode: 'auto' | 'refresh' = 'auto') => {
      const isInitial = mode === 'refresh' ? false : !hasSummaryLoadedRef.current;
      if (isInitial) {
        setSummaryLoading(true);
      }
      try {
        const response = await apiService.getActsSummary({
          from: filters.from,
          to: filters.to,
          status: filters.status,
          clientId: filters.clientId,
          responsibleId: filters.responsibleId,
          search: filters.search,
        });
        setSummary(response);
        hasSummaryLoadedRef.current = true;
      } catch (err) {
        if (!hasSummaryLoadedRef.current) {
          setSummary(null);
        }
      } finally {
        if (isInitial) {
          setSummaryLoading(false);
        }
      }
    },
    [filters.from, filters.to, filters.status, filters.clientId, filters.responsibleId, filters.search],
  );

  useEffect(() => {
    hasLoadedRef.current = false;
    setLoading(true);
    void fetchActs('auto');
  }, [filtersKey, fetchActs]);

  useEffect(() => {
    hasSummaryLoadedRef.current = false;
    void fetchSummary('auto');
  }, [summaryKey, fetchSummary]);

  const refresh = useCallback(async () => {
    await Promise.all([fetchActs('refresh'), fetchSummary('refresh')]);
  }, [fetchActs, fetchSummary]);

  const createAct = useCallback(
    async (payload: ActInput) => {
      const created = await apiService.createAct(payload);
      await refresh();
      return created;
    },
    [refresh],
  );

  const updateAct = useCallback(
    async (id: number, payload: ActInput) => {
      const updated = await apiService.updateAct(id, payload);
      await refresh();
      return updated;
    },
    [refresh],
  );

  const deleteAct = useCallback(
    async (id: number) => {
      await apiService.deleteAct(id);
      await refresh();
    },
    [refresh],
  );

  return {
    acts,
    loading,
    refreshing,
    error,
    summary,
    summaryLoading,
    pagination,
    refresh,
    createAct,
    updateAct,
    deleteAct,
  };
}
