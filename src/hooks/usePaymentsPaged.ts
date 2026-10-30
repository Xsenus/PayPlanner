import { awaitOwnedUiResult } from '../utils/StaleUiOperationError';
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { ClientRole, Payment, PaymentAdvanceReviewStatus, PaymentsListSummary, PaymentStatus } from '../types';

type UsePaymentsPagedParams = {
  from?: string;
  to?: string;
  clientId?: number;
  legalEntityId?: number;
  contractId?: number;
  invoiceId?: number;
  actId?: number;
  clientRole?: ClientRole;
  caseId?: number;
  clientIds?: string[];
  categoryTokens?: string[];
  type?: Payment['type'];
  status?: PaymentStatus;
  advanceReviewStatus?: PaymentAdvanceReviewStatus;
  minAmount?: number | null;
  maxAmount?: number | null;
  search?: string;
  sortBy?: 'date' | 'amount' | 'createdAt';
  sortDir?: 'asc' | 'desc';
  page: number;
  pageSize: number;
  enabled?: boolean;
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
      legalEntityId: params.legalEntityId,
      contractId: params.contractId,
      invoiceId: params.invoiceId,
      actId: params.actId,
      clientRole: params.clientRole,
      caseId: params.caseId,
      clientIds: params.clientIds?.join(',') || undefined,
      categoryTokens: params.categoryTokens?.join(',') || undefined,
      type: params.type,
      status: params.status,
      advanceReviewStatus: params.advanceReviewStatus,
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
      params.legalEntityId,
      params.contractId,
      params.invoiceId,
      params.actId,
      params.advanceReviewStatus,
      params.clientRole,
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
  const enabled = params.enabled ?? true;

  const fetchPage = useCallback(
    async (mode: 'initial' | 'refresh' = 'initial', isCurrent: () => boolean = () => true) => {
      if (!isCurrent()) return;
      abortRef.current?.abort();

      if (!enabled) {
        setPayments([]);
        setTotal(0);
        setSummary(EMPTY_SUMMARY);
        setLoadingInitial(false);
        setRefreshing(false);
        setError(null);
        return;
      }

      const controller = new AbortController();
      abortRef.current = controller;

      setError(null);
      if (mode === 'initial') {
        setLoadingInitial(true);
        setRefreshing(false);
      } else {
        setRefreshing(true);
      }

      try {
        const data = await apiService.getPaymentsV2(
          requestParams,
          { signal: controller.signal },
        );
        if (controller.signal.aborted || !isCurrent()) return;
        setPayments(data.items ?? []);
        setTotal(data.total ?? 0);
        setSummary(data.summary ?? EMPTY_SUMMARY);
      } catch (err) {
        if (controller.signal.aborted || !isCurrent()) return;
        setPayments([]);
        setTotal(0);
        setSummary(EMPTY_SUMMARY);
        setError(err instanceof Error ? err.message : 'Не удалось загрузить платежи.');
      } finally {
        if (abortRef.current === controller) {
          abortRef.current = null;
        }
        if (!controller.signal.aborted) {
          setLoadingInitial(false);
          setRefreshing(false);
        }
      }
    },
    [enabled, requestParams],
  );

  const currentFetchRef = useRef<typeof fetchPage | null>(null);

  useLayoutEffect(() => {
    currentFetchRef.current = fetchPage;
    return () => {
      currentFetchRef.current = null;
      abortRef.current?.abort();
    };
  }, [fetchPage]);

  useEffect(() => {
    void fetchPage('initial');
  }, [fetchPage]);

  const refresh = useCallback(async () => {
    await currentFetchRef.current?.('refresh');
  }, []);

  const refreshOwned = useCallback(async (isCurrent: () => boolean) => {
    await awaitOwnedUiResult(() => currentFetchRef.current?.('refresh', isCurrent) ?? Promise.resolve(), isCurrent);
  }, []);

  return {
    payments,
    total,
    summary,
    loading: loadingInitial,
    refreshing,
    error,
    refresh,
    refreshOwned,
  };
}
