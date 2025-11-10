import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { Contract, ContractInput } from '../types';

export type ContractsSortKey = 'date' | 'number' | 'amount' | 'createdAt';

export interface ContractsFilters {
  from?: string;
  to?: string;
  clientId?: number;
  search?: string;
  sortBy?: ContractsSortKey;
  sortDir?: 'asc' | 'desc';
  page?: number;
  pageSize?: number;
}

export interface ContractsPagination {
  page: number;
  pageSize: number;
  total: number;
}

interface UseContractsResult {
  contracts: Contract[];
  loading: boolean;
  refreshing: boolean;
  error: string | null;
  pagination: ContractsPagination;
  refresh: () => Promise<void>;
  createContract: (payload: ContractInput) => Promise<Contract>;
  updateContract: (id: number, payload: ContractInput) => Promise<Contract>;
  deleteContract: (id: number) => Promise<void>;
}

export function useContracts(filters: ContractsFilters): UseContractsResult {
  const [contracts, setContracts] = useState<Contract[]>([]);
  const [loading, setLoading] = useState<boolean>(true);
  const [refreshing, setRefreshing] = useState<boolean>(false);
  const [error, setError] = useState<string | null>(null);
  const [pagination, setPagination] = useState<ContractsPagination>({
    page: filters.page ?? 1,
    pageSize: filters.pageSize ?? 20,
    total: 0,
  });

  const hasLoadedRef = useRef(false);
  const filtersKey = useMemo(() => JSON.stringify(filters), [filters]);

  const fetchContracts = useCallback(
    async (mode: 'auto' | 'refresh' = 'auto') => {
      const isInitial = mode === 'refresh' ? false : !hasLoadedRef.current;
      if (isInitial) {
        setLoading(true);
      } else {
        setRefreshing(true);
      }
      setError(null);
      try {
        const response = await apiService.getContracts({
          from: filters.from,
          to: filters.to,
          clientId: filters.clientId,
          search: filters.search,
          sortBy: filters.sortBy,
          sortDir: filters.sortDir,
          page: filters.page,
          pageSize: filters.pageSize,
        });
        setContracts(response.items ?? []);
        setPagination({
          page: response.page ?? filters.page ?? 1,
          pageSize: response.pageSize ?? filters.pageSize ?? 20,
          total: response.total ?? 0,
        });
        hasLoadedRef.current = true;
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Не удалось загрузить договоры';
        setError(message);
        if (!hasLoadedRef.current) {
          setContracts([]);
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
    [filters.from, filters.to, filters.clientId, filters.search, filters.sortBy, filters.sortDir, filters.page, filters.pageSize],
  );

  useEffect(() => {
    hasLoadedRef.current = false;
    setLoading(true);
    void fetchContracts('auto');
  }, [filtersKey, fetchContracts]);

  const refresh = useCallback(async () => {
    await fetchContracts('refresh');
  }, [fetchContracts]);

  const createContract = useCallback(
    async (payload: ContractInput) => {
      const created = await apiService.createContract(payload);
      await refresh();
      return created;
    },
    [refresh],
  );

  const updateContract = useCallback(
    async (id: number, payload: ContractInput) => {
      const updated = await apiService.updateContract(id, payload);
      await refresh();
      return updated;
    },
    [refresh],
  );

  const deleteContract = useCallback(
    async (id: number) => {
      await apiService.deleteContract(id);
      await refresh();
    },
    [refresh],
  );

  return {
    contracts,
    loading,
    refreshing,
    error,
    pagination,
    refresh,
    createContract,
    updateContract,
    deleteContract,
  };
}
