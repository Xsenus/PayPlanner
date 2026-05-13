import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { AccountRegistryRow, AccountsSummary, ClientRole } from '../types';

type AccountRegistryStatus = AccountRegistryRow['status'];
type AccountRegistryType = AccountRegistryRow['type'];
type AccountRegistrySortBy = 'date' | 'number' | 'amount' | 'counterparty' | 'dueDate' | 'responsible';

type UseAccountsRegistryParams = {
  from?: string;
  to?: string;
  status?: AccountRegistryStatus | 'all';
  search?: string;
  type?: AccountRegistryType | 'All';
  clientId?: number;
  legalEntityId?: number;
  clientIds?: string;
  contractId?: number;
  clientRole?: ClientRole;
  payablesOnly?: boolean;
  sortBy?: AccountRegistrySortBy;
  sortDir?: 'asc' | 'desc';
  page: number;
  pageSize: number;
};

const EMPTY_SUMMARY: AccountsSummary = {
  total: { amount: 0, count: 0 },
  pending: { amount: 0, count: 0 },
  paid: { amount: 0, count: 0 },
  overdue: { amount: 0, count: 0 },
  cancelled: { amount: 0, count: 0 },
};

export function useAccountsRegistry(params: UseAccountsRegistryParams) {
  const [items, setItems] = useState<AccountRegistryRow[]>([]);
  const [total, setTotal] = useState(0);
  const [summary, setSummary] = useState<AccountsSummary>(EMPTY_SUMMARY);
  const [loadingInitial, setLoadingInitial] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const abortRef = useRef<AbortController | null>(null);

  const requestParams = useMemo(
    () => ({
      from: params.from,
      to: params.to,
      status: params.status && params.status !== 'all' ? params.status : undefined,
      search: params.search,
      type: params.type && params.type !== 'All' ? params.type : undefined,
      clientId: params.clientId,
      legalEntityId: params.legalEntityId,
      clientIds: params.clientIds,
      contractId: params.contractId,
      clientRole: params.clientRole,
      payablesOnly: params.payablesOnly || undefined,
      sortBy: params.sortBy,
      sortDir: params.sortDir,
      page: params.page,
      pageSize: params.pageSize,
    }),
    [
      params.clientId,
      params.legalEntityId,
      params.clientIds,
      params.contractId,
      params.clientRole,
      params.from,
      params.page,
      params.pageSize,
      params.payablesOnly,
      params.search,
      params.sortBy,
      params.sortDir,
      params.status,
      params.to,
      params.type,
    ],
  );

  const fetchPage = useCallback(
    async (mode: 'initial' | 'refresh' = 'initial') => {
      abortRef.current?.abort();
      const controller = new AbortController();
      abortRef.current = controller;

      setError(null);
      if (mode === 'initial') {
        setLoadingInitial(true);
      } else {
        setRefreshing(true);
      }

      try {
        const data = await apiService.getAccountsRegistry(requestParams);
        if (controller.signal.aborted) return;
        setItems(data.items ?? []);
        setTotal(data.total ?? 0);
        setSummary(data.summary ?? EMPTY_SUMMARY);
      } catch (err) {
        if (controller.signal.aborted) return;
        setItems([]);
        setTotal(0);
        setSummary(EMPTY_SUMMARY);
        setError(err instanceof Error ? err.message : 'Не удалось загрузить реестр счетов.');
      } finally {
        if (!controller.signal.aborted) {
          if (mode === 'initial') {
            setLoadingInitial(false);
          } else {
            setRefreshing(false);
          }
        }
      }
    },
    [requestParams],
  );

  useEffect(() => {
    void fetchPage('initial');
    return () => {
      abortRef.current?.abort();
    };
  }, [fetchPage]);

  const refresh = useCallback(async () => {
    await fetchPage('refresh');
  }, [fetchPage]);

  return {
    items,
    total,
    summary,
    loading: loadingInitial,
    refreshing,
    error,
    refresh,
  };
}
