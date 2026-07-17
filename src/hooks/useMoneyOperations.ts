import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type {
  MoneyOperation,
  MoneyOperationAccountingType,
  MoneyOperationDirection,
  MoneyOperationsSummary,
  MoneyOperationSource,
} from '../types';

export interface MoneyOperationsFilters {
  from?: string;
  to?: string;
  source?: MoneyOperationSource;
  direction?: MoneyOperationDirection;
  accountingType?: MoneyOperationAccountingType;
  moneyAccountId?: number;
  accountName?: string;
  categoryName?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

const EMPTY_SUMMARY: MoneyOperationsSummary = {
  startBalance: 0,
  income: 0,
  expense: 0,
  endBalance: 0,
  incomeCount: 0,
  expenseCount: 0,
  bySource: [],
};

export function useMoneyOperations(filters: MoneyOperationsFilters) {
  const [items, setItems] = useState<MoneyOperation[]>([]);
  const [total, setTotal] = useState(0);
  const [summary, setSummary] = useState<MoneyOperationsSummary>(EMPTY_SUMMARY);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestIdRef = useRef(0);
  const activeRequestControllerRef = useRef<AbortController | null>(null);

  const stableFilters = useMemo(
    () => ({
      from: filters.from,
      to: filters.to,
      source: filters.source,
      direction: filters.direction,
      accountingType: filters.accountingType,
      moneyAccountId: filters.moneyAccountId,
      accountName: filters.accountName,
      categoryName: filters.categoryName,
      search: filters.search,
      page: filters.page,
      pageSize: filters.pageSize,
    }),
    [
      filters.accountName,
      filters.accountingType,
      filters.categoryName,
      filters.direction,
      filters.from,
      filters.moneyAccountId,
      filters.page,
      filters.pageSize,
      filters.search,
      filters.source,
      filters.to,
    ],
  );

  const load = useCallback(async () => {
    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    activeRequestControllerRef.current?.abort();
    const controller = new AbortController();
    activeRequestControllerRef.current = controller;
    setLoading(true);
    setError(null);

    try {
      const data = await apiService.getMoneyOperationsPaged(
        stableFilters,
        { signal: controller.signal },
      );
      if (requestId !== requestIdRef.current || controller.signal.aborted) {
        return;
      }

      setItems(data.items);
      setTotal(data.total);
      setSummary(data.summary ?? EMPTY_SUMMARY);
    } catch (err) {
      if (requestId !== requestIdRef.current || controller.signal.aborted) {
        return;
      }

      const message = err instanceof Error ? err.message : 'Не удалось загрузить операции.';
      setError(message);
      setItems([]);
      setTotal(0);
      setSummary(EMPTY_SUMMARY);
    } finally {
      if (activeRequestControllerRef.current === controller) {
        activeRequestControllerRef.current = null;
      }
      if (requestId === requestIdRef.current) {
        setLoading(false);
      }
    }
  }, [stableFilters]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => () => {
    activeRequestControllerRef.current?.abort();
    activeRequestControllerRef.current = null;
    requestIdRef.current += 1;
  }, []);

  return {
    items,
    total,
    summary,
    loading,
    error,
    reload: load,
    setItems,
  };
}
