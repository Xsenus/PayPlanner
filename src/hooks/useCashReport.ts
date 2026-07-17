import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { CashBookSummary } from '../types';

export interface CashReportFilters {
  from?: string;
  to?: string;
  accountName?: string;
  counterpartyName?: string;
  clientId?: number;
  caseId?: number;
  contractId?: number;
  incomeTypeId?: number;
}

export function useCashReport(filters: CashReportFilters, options: { enabled?: boolean } = {}) {
  const enabled = options.enabled ?? true;
  const [report, setReport] = useState<CashBookSummary | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestIdRef = useRef(0);
  const abortRef = useRef<AbortController | null>(null);

  const stableFilters = useMemo(
    () => ({
      from: filters.from,
      to: filters.to,
      accountName: filters.accountName,
      counterpartyName: filters.counterpartyName,
      clientId: filters.clientId,
      caseId: filters.caseId,
      contractId: filters.contractId,
      incomeTypeId: filters.incomeTypeId,
    }),
    [
      filters.accountName,
      filters.caseId,
      filters.clientId,
      filters.contractId,
      filters.counterpartyName,
      filters.from,
      filters.incomeTypeId,
      filters.to,
    ],
  );

  const load = useCallback(async () => {
    abortRef.current?.abort();
    abortRef.current = null;
    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    if (!enabled) {
      setReport(null);
      setLoading(false);
      setError(null);
      return;
    }

    const controller = new AbortController();
    abortRef.current = controller;
    setLoading(true);
    setError(null);

    try {
      const data = await apiService.getCashBook(stableFilters, { signal: controller.signal });
      if (controller.signal.aborted || requestId !== requestIdRef.current) {
        return;
      }

      setReport(data);
    } catch (err) {
      if (controller.signal.aborted || requestId !== requestIdRef.current) {
        return;
      }

      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить отчет по кассе.');
    } finally {
      if (abortRef.current === controller) {
        abortRef.current = null;
      }
      if (!controller.signal.aborted && requestId === requestIdRef.current) {
        setLoading(false);
      }
    }
  }, [enabled, stableFilters]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => () => {
    abortRef.current?.abort();
    abortRef.current = null;
    requestIdRef.current += 1;
  }, []);

  return {
    report,
    loading,
    error,
    reload: load,
  };
}
