import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { MoneyOperationSource, PaidInvoiceReport } from '../types';

export interface PaidInvoiceReportFilters {
  from?: string;
  to?: string;
  source?: MoneyOperationSource;
  accountName?: string;
  counterpartyName?: string;
  clientId?: number;
}

export function usePaidInvoiceReport(filters: PaidInvoiceReportFilters) {
  const [report, setReport] = useState<PaidInvoiceReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestIdRef = useRef(0);

  const stableFilters = useMemo(
    () => ({
      from: filters.from,
      to: filters.to,
      source: filters.source,
      accountName: filters.accountName,
      counterpartyName: filters.counterpartyName,
      clientId: filters.clientId,
    }),
    [filters.accountName, filters.clientId, filters.counterpartyName, filters.from, filters.source, filters.to],
  );

  const load = useCallback(async () => {
    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    setLoading(true);
    setError(null);

    try {
      const data = await apiService.getPaidInvoiceReport(stableFilters);
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(data);
    } catch (err) {
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить отчет по оплаченным счетам.');
    } finally {
      if (requestId === requestIdRef.current) {
        setLoading(false);
      }
    }
  }, [stableFilters]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => () => {
    requestIdRef.current += 1;
  }, []);

  return {
    report,
    loading,
    error,
    reload: load,
  };
}
