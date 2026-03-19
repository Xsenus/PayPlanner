import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { Payment, PaymentsListSummary, PaymentStatus } from '../types';

type UsePaymentsPagedParams = {
  from?: string;
  to?: string;
  clientId?: number;
  caseId?: number;
  clientIds?: string[];
  categoryTokens?: string[];
  type?: Payment['type'];
  status?: PaymentStatus;
  minAmount?: number | null;
  maxAmount?: number | null;
  search?: string;
  sortBy?: 'date' | 'amount' | 'createdAt';
  sortDir?: 'asc' | 'desc';
  page: number;
  pageSize: number;
};

const EMPTY_SUMMARY: PaymentsListSummary = {
  income: 0,
  expense: 0,
  balance: 0,
  incomeCount: 0,
  expenseCount: 0,
  total: 0,
};

export function usePaymentsPaged(params: UsePaymentsPagedParams) {
  const [payments, setPayments] = useState<Payment[]>([]);
  const [total, setTotal] = useState(0);
  const [summary, setSummary] = useState<PaymentsListSummary>(EMPTY_SUMMARY);
  const [loadingInitial, setLoadingInitial] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const abortRef = useRef<AbortController | null>(null);

  const requestParams = useMemo(
    () => ({
      from: params.from,
      to: params.to,
      clientId: params.clientId,
      caseId: params.caseId,
      clientIds: params.clientIds?.join(',') || undefined,
      categoryTokens: params.categoryTokens?.join(',') || undefined,
      type: params.type,
      status: params.status,
      minAmount: params.minAmount ?? undefined,
      maxAmount: params.maxAmount ?? undefined,
      search: params.search,
      sortBy: params.sortBy,
      sortDir: params.sortDir,
      page: params.page,
      pageSize: params.pageSize,
    }),
    [
      params.caseId,
      params.categoryTokens,
      params.clientId,
      params.clientIds,
      params.from,
      params.maxAmount,
      params.minAmount,
      params.page,
      params.pageSize,
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
        const data = await apiService.getPaymentsV2(requestParams);
        if (controller.signal.aborted) return;
        setPayments(data.items ?? []);
        setTotal(data.total ?? 0);
        setSummary(data.summary ?? EMPTY_SUMMARY);
      } catch (err) {
        if (controller.signal.aborted) return;
        setPayments([]);
        setTotal(0);
        setSummary(EMPTY_SUMMARY);
        setError(err instanceof Error ? err.message : 'Не удалось загрузить платежи.');
      } finally {
        if (controller.signal.aborted) return;
        if (mode === 'initial') {
          setLoadingInitial(false);
        } else {
          setRefreshing(false);
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
    payments,
    total,
    summary,
    loading: loadingInitial,
    refreshing,
    error,
    refresh,
  };
}
