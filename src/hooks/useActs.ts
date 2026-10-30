import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { Act, ActInput, ActStatus, ActWorkflowStatus, ActsSummary, PaymentKind } from '../types';
import { normalizeActArray, normalizeActsSummary } from '../utils/acts';
import { extractDisplayErrorMessage } from '../utils/errorUtils';
import { awaitOwnedUiResult } from '../utils/StaleUiOperationError';

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
  workflowStatus?: ActWorkflowStatus;
  type?: PaymentKind;
  clientId?: number;
  legalEntityId?: number;
  contractId?: number;
  invoiceId?: number;
  paymentId?: number;
  caseId?: number;
  responsibleId?: number;
  search?: string;
  sortBy?: ActsSortKey;
  sortDir?: 'asc' | 'desc';
  page?: number;
  pageSize?: number;
  enabled?: boolean;
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
  refreshOwned: (isCurrent: () => boolean) => Promise<void>;
  createAct: (payload: ActInput, isCurrent?: () => boolean) => Promise<Act>;
  updateAct: (id: number, payload: ActInput, isCurrent?: () => boolean) => Promise<Act>;
  deleteAct: (id: number, isCurrent?: () => boolean) => Promise<void>;
}

function makeSummaryKey(filters: ActsFilters): string {
  return JSON.stringify({
    from: filters.from ?? null,
    to: filters.to ?? null,
    status: filters.status ?? null,
    workflowStatus: filters.workflowStatus ?? null,
    type: filters.type ?? null,
    clientId: filters.clientId ?? null,
    legalEntityId: filters.legalEntityId ?? null,
    contractId: filters.contractId ?? null,
    invoiceId: filters.invoiceId ?? null,
    paymentId: filters.paymentId ?? null,
    caseId: filters.caseId ?? null,
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
  const actsAbortRef = useRef<AbortController | null>(null);
  const summaryAbortRef = useRef<AbortController | null>(null);
  const enabled = filters.enabled ?? true;

  const filtersKey = useMemo(() => JSON.stringify(filters), [filters]);
  const summaryKey = useMemo(() => makeSummaryKey(filters), [filters]);

  const fetchActs = useCallback(
    async (mode: 'auto' | 'refresh' = 'auto', isCurrent: () => boolean = () => true) => {
      if (!isCurrent()) return;
      actsAbortRef.current?.abort();
      actsAbortRef.current = null;
      if (!enabled) {
        setActs([]);
        setPagination({ page: filters.page ?? 1, pageSize: filters.pageSize ?? 20, total: 0 });
        setLoading(false);
        setRefreshing(false);
        setError(null);
        return;
      }

      const controller = new AbortController();
      actsAbortRef.current = controller;

      const isInitial = mode === 'refresh' ? false : !hasLoadedRef.current;
      if (isInitial) {
        setLoading(true);
      } else {
        setRefreshing(true);
      }
      setError(null);
      try {
        const response = await apiService.getActs(
          {
            from: filters.from,
            to: filters.to,
            status: filters.status,
            workflowStatus: filters.workflowStatus,
            type: filters.type,
            clientId: filters.clientId,
            legalEntityId: filters.legalEntityId,
            contractId: filters.contractId,
            invoiceId: filters.invoiceId,
            paymentId: filters.paymentId,
            caseId: filters.caseId,
            responsibleId: filters.responsibleId,
            search: filters.search,
            sortBy: filters.sortBy,
            sortDir: filters.sortDir,
            page: filters.page,
            pageSize: filters.pageSize,
          },
          { signal: controller.signal },
        );
        if (controller.signal.aborted || !isCurrent()) return;
        setActs(normalizeActArray(response?.items));
        setPagination({
          page: Number(response?.page ?? filters.page ?? 1) || 1,
          pageSize: Number(response?.pageSize ?? filters.pageSize ?? 20) || 20,
          total: Number(response?.total ?? 0) || 0,
        });
        hasLoadedRef.current = true;
      } catch (err) {
        if (controller.signal.aborted || !isCurrent()) return;
        const message = extractDisplayErrorMessage(err, 'Не удалось загрузить акты');
        setError(message);
        if (!hasLoadedRef.current) {
          setActs([]);
          setPagination({ page: filters.page ?? 1, pageSize: filters.pageSize ?? 20, total: 0 });
        }
      } finally {
        if (actsAbortRef.current === controller) {
          actsAbortRef.current = null;
        }
        if (!controller.signal.aborted) {
          setLoading(false);
          setRefreshing(false);
        }
      }
    },
    [enabled, filters.from, filters.to, filters.status, filters.workflowStatus, filters.type, filters.clientId, filters.legalEntityId, filters.contractId, filters.invoiceId, filters.paymentId, filters.caseId, filters.responsibleId, filters.search, filters.sortBy, filters.sortDir, filters.page, filters.pageSize],
  );

  const fetchSummary = useCallback(
    async (mode: 'auto' | 'refresh' = 'auto', isCurrent: () => boolean = () => true) => {
      if (!isCurrent()) return;
      summaryAbortRef.current?.abort();
      summaryAbortRef.current = null;
      if (!enabled) {
        setSummary(null);
        setSummaryLoading(false);
        return;
      }

      const controller = new AbortController();
      summaryAbortRef.current = controller;

      const isInitial = mode === 'refresh' ? false : !hasSummaryLoadedRef.current;
      if (isInitial) {
        setSummaryLoading(true);
      }
      try {
        const response = await apiService.getActsSummary(
          {
            from: filters.from,
            to: filters.to,
            status: filters.status,
            workflowStatus: filters.workflowStatus,
            type: filters.type,
            clientId: filters.clientId,
            legalEntityId: filters.legalEntityId,
            contractId: filters.contractId,
            invoiceId: filters.invoiceId,
            paymentId: filters.paymentId,
            caseId: filters.caseId,
            responsibleId: filters.responsibleId,
            search: filters.search,
          },
          { signal: controller.signal },
        );
        if (controller.signal.aborted || !isCurrent()) return;
        setSummary(normalizeActsSummary(response));
        hasSummaryLoadedRef.current = true;
      } catch {
        if (controller.signal.aborted || !isCurrent()) return;
        if (!hasSummaryLoadedRef.current) {
          setSummary(null);
        }
      } finally {
        if (summaryAbortRef.current === controller) {
          summaryAbortRef.current = null;
        }
        if (!controller.signal.aborted) {
          setSummaryLoading(false);
        }
      }
    },
    [enabled, filters.from, filters.to, filters.status, filters.workflowStatus, filters.type, filters.clientId, filters.legalEntityId, filters.contractId, filters.invoiceId, filters.paymentId, filters.caseId, filters.responsibleId, filters.search],
  );

  useEffect(() => {
    hasLoadedRef.current = false;
    setLoading(true);
    void fetchActs('auto');
    return () => {
      actsAbortRef.current?.abort();
    };
  }, [filtersKey, fetchActs]);

  useEffect(() => {
    hasSummaryLoadedRef.current = false;
    void fetchSummary('auto');
    return () => {
      summaryAbortRef.current?.abort();
    };
  }, [summaryKey, fetchSummary]);

  const refresh = useCallback(async () => {
    await Promise.all([fetchActs('refresh'), fetchSummary('refresh')]);
  }, [fetchActs, fetchSummary]);

  const refreshOwned = useCallback(async (isCurrent: () => boolean) => {
    await awaitOwnedUiResult(() => Promise.all([fetchActs('refresh', isCurrent), fetchSummary('refresh', isCurrent)]), isCurrent);
  }, [fetchActs, fetchSummary]);

  const createAct = useCallback(
    async (payload: ActInput, isCurrent: () => boolean = () => true) => {
      const created = await awaitOwnedUiResult(() => apiService.createAct(payload), isCurrent);
      await refreshOwned(isCurrent);
      return created;
    },
    [refreshOwned],
  );

  const updateAct = useCallback(
    async (id: number, payload: ActInput, isCurrent: () => boolean = () => true) => {
      const updated = await awaitOwnedUiResult(() => apiService.updateAct(id, payload), isCurrent);
      await refreshOwned(isCurrent);
      return updated;
    },
    [refreshOwned],
  );

  const deleteAct = useCallback(
    async (id: number, isCurrent: () => boolean = () => true) => {
      await awaitOwnedUiResult(() => apiService.deleteAct(id), isCurrent);
      await refreshOwned(isCurrent);
    },
    [refreshOwned],
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
    refreshOwned,
    createAct,
    updateAct,
    deleteAct,
  };
}
