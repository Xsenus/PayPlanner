import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { Invoice, InvoiceClientRole, InvoiceInput, InvoiceSummary, InvoiceWorkflowStatus, PaymentKind, PaymentStatus } from '../types';
import { extractDisplayErrorMessage } from '../utils/errorUtils';

export type InvoicesSortKey =
  | 'date'
  | 'number'
  | 'amount'
  | 'dueDate'
  | 'status'
  | 'client'
  | 'counterparty'
  | 'responsible'
  | 'createdAt';

export interface InvoicesFilters {
  from?: string;
  to?: string;
  status?: PaymentStatus;
  workflowStatus?: InvoiceWorkflowStatus;
  clientId?: number;
  legalEntityId?: number;
  contractId?: number;
  actId?: number;
  paymentId?: number;
  caseId?: number;
  clientRole?: InvoiceClientRole;
  responsibleId?: number;
  search?: string;
  type?: PaymentKind;
  sortBy?: InvoicesSortKey;
  sortDir?: 'asc' | 'desc';
  page?: number;
  pageSize?: number;
  enabled?: boolean;
}

export interface InvoicesPagination {
  page: number;
  pageSize: number;
  total: number;
}

interface UseInvoicesResult {
  invoices: Invoice[];
  loading: boolean;
  refreshing: boolean;
  error: string | null;
  summary: InvoiceSummary | null;
  summaryLoading: boolean;
  pagination: InvoicesPagination;
  refresh: () => Promise<void>;
  createInvoice: (payload: InvoiceInput) => Promise<Invoice>;
  updateInvoice: (id: number, payload: InvoiceInput) => Promise<Invoice>;
  deleteInvoice: (id: number) => Promise<void>;
}

function makeSummaryKey(filters: InvoicesFilters): string {
  return JSON.stringify({
    from: filters.from ?? null,
    to: filters.to ?? null,
    status: filters.status ?? null,
    clientId: filters.clientId ?? null,
    legalEntityId: filters.legalEntityId ?? null,
    contractId: filters.contractId ?? null,
    actId: filters.actId ?? null,
    paymentId: filters.paymentId ?? null,
    caseId: filters.caseId ?? null,
    clientRole: filters.clientRole ?? null,
    responsibleId: filters.responsibleId ?? null,
    search: filters.search ?? null,
    type: filters.type ?? null,
  });
}

export function useInvoices(filters: InvoicesFilters): UseInvoicesResult {
  const [invoices, setInvoices] = useState<Invoice[]>([]);
  const [loading, setLoading] = useState<boolean>(true);
  const [refreshing, setRefreshing] = useState<boolean>(false);
  const [error, setError] = useState<string | null>(null);
  const [summary, setSummary] = useState<InvoiceSummary | null>(null);
  const [summaryLoading, setSummaryLoading] = useState<boolean>(false);
  const [pagination, setPagination] = useState<InvoicesPagination>({
    page: filters.page ?? 1,
    pageSize: filters.pageSize ?? 20,
    total: 0,
  });

  const hasLoadedRef = useRef(false);
  const hasSummaryLoadedRef = useRef(false);
  const invoicesAbortRef = useRef<AbortController | null>(null);
  const summaryAbortRef = useRef<AbortController | null>(null);
  const enabled = filters.enabled ?? true;

  const filtersKey = useMemo(() => JSON.stringify(filters), [filters]);
  const summaryKey = useMemo(() => makeSummaryKey(filters), [filters]);

  const fetchInvoices = useCallback(
    async (mode: 'auto' | 'refresh' = 'auto') => {
      invoicesAbortRef.current?.abort();
      invoicesAbortRef.current = null;
      if (!enabled) {
        setInvoices([]);
        setPagination({ page: filters.page ?? 1, pageSize: filters.pageSize ?? 20, total: 0 });
        setLoading(false);
        setRefreshing(false);
        setError(null);
        return;
      }

      const controller = new AbortController();
      invoicesAbortRef.current = controller;

      const isInitial = mode === 'refresh' ? false : !hasLoadedRef.current;
      if (isInitial) {
        setLoading(true);
      } else {
        setRefreshing(true);
      }
      setError(null);
      try {
        const response = await apiService.getInvoices(
          {
            from: filters.from,
            to: filters.to,
            status: filters.status,
            clientId: filters.clientId,
            legalEntityId: filters.legalEntityId,
            contractId: filters.contractId,
            actId: filters.actId,
            paymentId: filters.paymentId,
            caseId: filters.caseId,
            clientRole: filters.clientRole,
            responsibleId: filters.responsibleId,
            search: filters.search,
            type: filters.type,
            sortBy: filters.sortBy,
            sortDir: filters.sortDir,
            page: filters.page,
            pageSize: filters.pageSize,
          },
          { signal: controller.signal },
        );
        if (controller.signal.aborted) return;
        setInvoices(response.items ?? []);
        setPagination({
          page: response.page ?? filters.page ?? 1,
          pageSize: response.pageSize ?? filters.pageSize ?? 20,
          total: response.total ?? 0,
        });
        hasLoadedRef.current = true;
      } catch (err) {
        if (controller.signal.aborted) return;
        const message = extractDisplayErrorMessage(err, 'Не удалось загрузить счета');
        setError(message);
        if (!hasLoadedRef.current) {
          setInvoices([]);
          setPagination({ page: filters.page ?? 1, pageSize: filters.pageSize ?? 20, total: 0 });
        }
      } finally {
        if (invoicesAbortRef.current === controller) {
          invoicesAbortRef.current = null;
        }
        if (!controller.signal.aborted) {
          setLoading(false);
          setRefreshing(false);
        }
      }
    },
    [
      filters.from,
      filters.to,
      filters.status,
      filters.clientId,
      filters.legalEntityId,
      filters.contractId,
      filters.actId,
      filters.paymentId,
      filters.caseId,
      filters.clientRole,
      filters.responsibleId,
      filters.search,
      filters.type,
      filters.sortBy,
      filters.sortDir,
      filters.page,
      filters.pageSize,
      enabled,
    ],
  );

  const fetchSummary = useCallback(
    async (mode: 'auto' | 'refresh' = 'auto') => {
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
        const response = await apiService.getInvoicesSummary(
          {
            from: filters.from,
            to: filters.to,
            status: filters.status,
            clientId: filters.clientId,
            legalEntityId: filters.legalEntityId,
            contractId: filters.contractId,
            actId: filters.actId,
            paymentId: filters.paymentId,
            caseId: filters.caseId,
            clientRole: filters.clientRole,
            responsibleId: filters.responsibleId,
            search: filters.search,
            type: filters.type,
          },
          { signal: controller.signal },
        );
        if (controller.signal.aborted) return;
        setSummary(response);
        hasSummaryLoadedRef.current = true;
      } catch {
        if (controller.signal.aborted) return;
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
    [
      filters.from,
      filters.to,
      filters.status,
      filters.clientId,
      filters.legalEntityId,
      filters.contractId,
      filters.actId,
      filters.paymentId,
      filters.caseId,
      filters.clientRole,
      filters.responsibleId,
      filters.search,
      filters.type,
      enabled,
    ],
  );

  useEffect(() => {
    hasLoadedRef.current = false;
    setLoading(true);
    void fetchInvoices('auto');
    return () => {
      invoicesAbortRef.current?.abort();
    };
  }, [filtersKey, fetchInvoices]);

  useEffect(() => {
    hasSummaryLoadedRef.current = false;
    void fetchSummary('auto');
    return () => {
      summaryAbortRef.current?.abort();
    };
  }, [summaryKey, fetchSummary]);

  const refresh = useCallback(async () => {
    await Promise.all([fetchInvoices('refresh'), fetchSummary('refresh')]);
  }, [fetchInvoices, fetchSummary]);

  const createInvoice = useCallback(
    async (payload: InvoiceInput) => {
      const created = await apiService.createInvoice(payload);
      await refresh();
      return created;
    },
    [refresh],
  );

  const updateInvoice = useCallback(
    async (id: number, payload: InvoiceInput) => {
      const updated = await apiService.updateInvoice(id, payload);
      await refresh();
      return updated;
    },
    [refresh],
  );

  const deleteInvoice = useCallback(
    async (id: number) => {
      await apiService.deleteInvoice(id);
      await refresh();
    },
    [refresh],
  );

  return {
    invoices,
    loading,
    refreshing,
    error,
    summary,
    summaryLoading,
    pagination,
    refresh,
    createInvoice,
    updateInvoice,
    deleteInvoice,
  };
}
