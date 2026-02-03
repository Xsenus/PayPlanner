import { useCallback, useEffect, useMemo, useState } from 'react';
import { apiService } from '../services/api';
import type { MoneyOperation, MoneyOperationAccountingType, MoneyOperationDirection, MoneyOperationSource } from '../types';

export interface MoneyOperationsFilters {
  from?: string;
  to?: string;
  source?: MoneyOperationSource;
  direction?: MoneyOperationDirection;
  accountingType?: MoneyOperationAccountingType;
  search?: string;
}

export function useMoneyOperations(filters: MoneyOperationsFilters) {
  const [items, setItems] = useState<MoneyOperation[]>([]);
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
    }),
    [
      filters.from,
      filters.to,
      filters.source,
      filters.direction,
      filters.accountingType,
      filters.search,
    ],
  );

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await apiService.getMoneyOperations(stableFilters);
      setItems(data);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Не удалось загрузить операции.';
      setError(message);
    } finally {
      setLoading(false);
    }
  }, [stableFilters]);

  useEffect(() => {
    void load();
  }, [load]);

  return { items, loading, error, reload: load, setItems };
}
