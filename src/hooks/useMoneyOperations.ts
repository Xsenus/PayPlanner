import { useCallback, useEffect, useMemo, useState } from 'react';
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

  const stableFilters = useMemo(
    () => ({
      from: filters.from,
      to: filters.to,
      source: filters.source,
      direction: filters.direction,
      accountingType: filters.accountingType,
      search: filters.search,
      page: filters.page,
      pageSize: filters.pageSize,
    }),
    [
      filters.accountingType,
      filters.direction,
      filters.from,
      filters.page,
      filters.pageSize,
      filters.search,
      filters.source,
      filters.to,
    ],
  );

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);

    try {
      const data = await apiService.getMoneyOperationsPaged(stableFilters);
      setItems(data.items);
      setTotal(data.total);
      setSummary(data.summary ?? EMPTY_SUMMARY);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Не удалось загрузить операции.';
      setError(message);
      setItems([]);
      setTotal(0);
      setSummary(EMPTY_SUMMARY);
    } finally {
      setLoading(false);
    }
  }, [stableFilters]);

  useEffect(() => {
    void load();
  }, [load]);

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
