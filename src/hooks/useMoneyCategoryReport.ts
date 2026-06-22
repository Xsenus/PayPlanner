import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { MoneyCategoryReport, MoneyOperationSource } from '../types';

export interface MoneyCategoryReportFilters {
  from?: string;
  to?: string;
  source?: MoneyOperationSource;
  accountName?: string;
  counterpartyName?: string;
  clientId?: number;
  caseId?: number;
  contractId?: number;
  incomeTypeId?: number;
}

export interface MoneyCategoryReportOptions {
  enabled?: boolean;
}

export function useMoneyCategoryReport(filters: MoneyCategoryReportFilters, options: MoneyCategoryReportOptions = {}) {
  const enabled = options.enabled ?? true;
  const [report, setReport] = useState<MoneyCategoryReport | null>(null);
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
      caseId: filters.caseId,
      contractId: filters.contractId,
      incomeTypeId: filters.incomeTypeId,
    }),
    [filters.accountName, filters.caseId, filters.clientId, filters.contractId, filters.incomeTypeId, filters.counterpartyName, filters.from, filters.source, filters.to],
  );

  const load = useCallback(async () => {
    if (!enabled) {
      requestIdRef.current += 1;
      setReport(null);
      setLoading(false);
      setError(null);
      return;
    }

    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    setLoading(true);
    setError(null);

    try {
      const data = await apiService.getMoneyCategoryReport(stableFilters);
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(data);
    } catch (err) {
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить отчет по статьям.');
    } finally {
      if (requestId === requestIdRef.current) {
        setLoading(false);
      }
    }
  }, [enabled, stableFilters]);

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
