import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { BankStatementImportReport } from '../types';

export interface BankStatementImportReportFilters {
  from?: string;
  to?: string;
  accountName?: string;
  counterpartyName?: string;
  clientId?: number;
  caseId?: number;
  contractId?: number;
  incomeTypeId?: number;
}

export function useBankStatementImportReport(filters: BankStatementImportReportFilters, options: { enabled?: boolean } = {}) {
  const enabled = options.enabled ?? true;
  const [report, setReport] = useState<BankStatementImportReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestIdRef = useRef(0);

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
    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;
    if (!enabled) {
      setReport(null);
      setLoading(false);
      setError(null);
      return;
    }

    setLoading(true);
    setError(null);

    try {
      const data = await apiService.getBankStatementImportReport(stableFilters);
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(data);
    } catch (err) {
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить отчет по импортам банковских выписок.');
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
