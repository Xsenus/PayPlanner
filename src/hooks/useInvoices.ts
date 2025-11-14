import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { Invoice, InvoiceInput, InvoiceSummary, PaymentKind, PaymentStatus } from '../types';

export type InvoicesSortKey =
  | 'date'
  | 'number'
  | 'amount'
  | 'dueDate'
  | 'status'
  | 'client'
  | 'responsible'
  | 'createdAt';

export interface InvoicesFilters {
  from?: string;
  to?: string;
  status?: PaymentStatus;
  type?: PaymentKind;
  clientId?: number;
  responsibleId?: number;
  search?: string;
  sortBy?: InvoicesSortKey;
  sortDir?: 'asc' | 'desc';
  page?: number;
  pageSize?: number;
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
    type: filters.type ?? null,
    clientId: filters.clientId ?? null,
    responsibleId: filters.responsibleId ?? null,
    search: filters.search ?? null,
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

  const filtersKey = useMemo(() => JSON.stringify(filters), [filters]);
  const summaryKey = useMemo(() => makeSummaryKey(filters), [filters]);

  const fetchInvoices = useCallback(
    async (mode: 'auto' | 'refresh' = 'auto') => {
      const isInitial = mode === 'refresh' ? false : !hasLoadedRef.current;
      if (isInitial) {
        setLoading(true);
      } else {
        setRefreshing(true);
      }
      setError(null);
      try {
        const response = await apiService.getInvoices({
          from: filters.from,
          to: filters.to,
          status: filters.status,
          type: filters.type,
          clientId: filters.clientId,
          responsibleId: filters.responsibleId,
          search: filters.search,
          sortBy: filters.sortBy,
          sortDir: filters.sortDir,
          page: filters.page,
          pageSize: filters.pageSize,
        });
        setInvoices(response.items ?? []);
        setPagination({
          page: response.page ?? filters.page ?? 1,
          pageSize: response.pageSize ?? filters.pageSize ?? 20,
          total: response.total ?? 0,
        });
        hasLoadedRef.current = true;
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Не удалось загрузить счета';
        setError(message);
        if (!hasLoadedRef.current) {
          setInvoices([]);
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
    [
      filters.from,
      filters.to,
      filters.status,
      filters.type,
      filters.clientId,
      filters.responsibleId,
      filters.search,
      filters.sortBy,
      filters.sortDir,
      filters.page,
      filters.pageSize,
    ],
  );

  const fetchSummary = useCallback(
    async (mode: 'auto' | 'refresh' = 'auto') => {
      const isInitial = mode === 'refresh' ? false : !hasSummaryLoadedRef.current;
      if (isInitial) {
        setSummaryLoading(true);
      }
      try {
        const response = await apiService.getInvoicesSummary({
          from: filters.from,
          to: filters.to,
          status: filters.status,
          type: filters.type,
          clientId: filters.clientId,
          responsibleId: filters.responsibleId,
          search: filters.search,
        });
        setSummary(response);
        hasSummaryLoadedRef.current = true;
      } catch {
        if (!hasSummaryLoadedRef.current) {
          setSummary(null);
        }
      } finally {
        if (isInitial) {
          setSummaryLoading(false);
        }
      }
    },
    [filters.from, filters.to, filters.status, filters.type, filters.clientId, filters.responsibleId, filters.search],
  );

  useEffect(() => {
    hasLoadedRef.current = false;
    setLoading(true);
    void fetchInvoices('auto');
  }, [filtersKey, fetchInvoices]);

  useEffect(() => {
    hasSummaryLoadedRef.current = false;
    void fetchSummary('auto');
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
