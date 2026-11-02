import type { DocumentReadOwner } from './useDocumentDeliveryOwner';
import { awaitOwnedUiResult } from '../utils/StaleUiOperationError';
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { Contract, ContractInput, ContractStatus } from '../types';
import { extractDisplayErrorMessage } from '../utils/errorUtils';

export type ContractsSortKey = 'date' | 'number' | 'amount' | 'createdAt';

export interface ContractsFilters {
  from?: string;
  to?: string;
  clientId?: number;
  invoiceId?: number;
  actId?: number;
  paymentId?: number;
  status?: ContractStatus;
  search?: string;
  sortBy?: ContractsSortKey;
  sortDir?: 'asc' | 'desc';
  page?: number;
  pageSize?: number;
  enabled?: boolean;
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
  refreshOwned: (isCurrent: () => boolean) => Promise<void>;
  createContract: (payload: ContractInput, isCurrent?: () => boolean) => Promise<Contract>;
  updateContract: (id: number, payload: ContractInput, isCurrent?: () => boolean) => Promise<Contract>;
  deleteContract: (id: number, isCurrent?: () => boolean) => Promise<void>;
}

export function useContracts(filters: ContractsFilters, readOwner?: DocumentReadOwner): UseContractsResult {
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
  const abortRef = useRef<AbortController | null>(null);
  const filtersKey = useMemo(() => JSON.stringify(filters), [filters]);
  const enabled = filters.enabled ?? true;
  const readScope = readOwner?.scope;
  const captureReadOwner = readOwner?.capture;
  const [dataScope, setDataScope] = useState(readScope);
  const resetReadScope = useRef<() => void>(() => undefined);
  resetReadScope.current = () => {
    abortRef.current?.abort(); abortRef.current = null; hasLoadedRef.current = false;
    setContracts([]); setError(null); setLoading(enabled); setRefreshing(false);
    setPagination({ page: filters.page ?? 1, pageSize: filters.pageSize ?? 20, total: 0 }); setDataScope(readScope);
  };
  useLayoutEffect(() => { if (readScope !== undefined) resetReadScope.current(); }, [readScope]);
  const visible = readScope === undefined || dataScope === readScope;


  const fetchContracts = useCallback(
    async (mode: 'auto' | 'refresh' = 'auto', ownsOperation: () => boolean = () => true) => {
      const owner = captureReadOwner?.();
      const ownsRead = () => !captureReadOwner || owner?.isCurrent() === true;
      const isCurrent = () => ownsRead() && ownsOperation();
      if (!isCurrent()) return;
      abortRef.current?.abort();
      abortRef.current = null;
      if (!enabled) {
        setContracts([]);
        setPagination({ page: filters.page ?? 1, pageSize: filters.pageSize ?? 20, total: 0 });
        setLoading(false);
        setRefreshing(false);
        setError(null);
        return;
      }

      const controller = new AbortController();
      abortRef.current = controller;

      const isInitial = mode === 'refresh' ? false : !hasLoadedRef.current;
      if (isInitial) {
        setLoading(true);
      } else {
        setRefreshing(true);
      }
      setError(null);
      try {
        const response = await apiService.getContracts(
          {
            from: filters.from,
            to: filters.to,
            clientId: filters.clientId,
            invoiceId: filters.invoiceId,
            actId: filters.actId,
            paymentId: filters.paymentId,
            status: filters.status,
            search: filters.search,
            sortBy: filters.sortBy,
            sortDir: filters.sortDir,
            page: filters.page,
            pageSize: filters.pageSize,
          },
          { signal: controller.signal },
        );
        if (controller.signal.aborted || !isCurrent()) return;
        setContracts(response.items ?? []);
        setPagination({
          page: response.page ?? filters.page ?? 1,
          pageSize: response.pageSize ?? filters.pageSize ?? 20,
          total: response.total ?? 0,
        });
        hasLoadedRef.current = true;
      } catch (err) {
        if (controller.signal.aborted || !isCurrent()) return;
        const message = extractDisplayErrorMessage(err, 'Не удалось загрузить договоры');
        setError(message);
        if (!hasLoadedRef.current) {
          setContracts([]);
          setPagination({ page: filters.page ?? 1, pageSize: filters.pageSize ?? 20, total: 0 });
        }
      } finally {
        if (abortRef.current === controller) {
          abortRef.current = null;
        }
        if (!controller.signal.aborted && ownsRead()) {
          setLoading(false);
          setRefreshing(false);
        }
      }
    },
    [enabled, filters.from, filters.to, filters.clientId, filters.invoiceId, filters.actId, filters.paymentId, filters.status, filters.search, filters.sortBy, filters.sortDir, filters.page, filters.pageSize, captureReadOwner],
  );

  useEffect(() => {
    hasLoadedRef.current = false;
    setLoading(true);
    void fetchContracts('auto');
    return () => {
      abortRef.current?.abort();
    };
  }, [filtersKey, fetchContracts]);

  const refresh = useCallback(async () => {
    await fetchContracts('refresh');
  }, [fetchContracts]);

  const refreshOwned = useCallback(async (isCurrent: () => boolean) => {
    await awaitOwnedUiResult(() => fetchContracts('refresh', isCurrent), isCurrent);
  }, [fetchContracts]);

  const createContract = useCallback(
    async (payload: ContractInput, isCurrent: () => boolean = () => true) => {
      const created = await awaitOwnedUiResult(() => apiService.createContract(payload), isCurrent);
      await refreshOwned(isCurrent);
      return created;
    },
    [refreshOwned],
  );

  const updateContract = useCallback(
    async (id: number, payload: ContractInput, isCurrent: () => boolean = () => true) => {
      const updated = await awaitOwnedUiResult(() => apiService.updateContract(id, payload), isCurrent);
      await refreshOwned(isCurrent);
      return updated;
    },
    [refreshOwned],
  );

  const deleteContract = useCallback(
    async (id: number, isCurrent: () => boolean = () => true) => {
      await awaitOwnedUiResult(() => apiService.deleteContract(id), isCurrent);
      await refreshOwned(isCurrent);
    },
    [refreshOwned],
  );

  return {
    contracts: visible ? contracts : [],
    loading: visible ? loading : enabled,
    refreshing: visible && refreshing,
    error: visible ? error : null,
    pagination: visible ? pagination : { page: filters.page ?? 1, pageSize: filters.pageSize ?? 20, total: 0 },
    refresh,
    refreshOwned,
    createContract,
    updateContract,
    deleteContract,
  };
}
