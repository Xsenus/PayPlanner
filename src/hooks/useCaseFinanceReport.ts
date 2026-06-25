import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../services/api';
import type { CaseFinanceReport } from '../types';

export interface CaseFinanceReportFilters {
  from?: string;
  to?: string;
  status?: string;
  responsibleUserId?: number;
  caseType?: string;
  limit?: number;
}

export interface CaseFinanceReportOptions {
  enabled?: boolean;
}

export function useCaseFinanceReport(filters: CaseFinanceReportFilters, options: CaseFinanceReportOptions = {}) {
  const enabled = options.enabled ?? true;
  const [report, setReport] = useState<CaseFinanceReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestIdRef = useRef(0);

  const stableFilters = useMemo(
    () => ({
      from: filters.from,
      to: filters.to,
      status: filters.status,
      responsibleUserId: filters.responsibleUserId,
      caseType: filters.caseType,
      limit: filters.limit,
    }),
    [filters.from, filters.to, filters.status, filters.responsibleUserId, filters.caseType, filters.limit],
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
      const data = await apiService.getCaseFinanceReport(stableFilters);
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(data);
    } catch (err) {
      if (requestId !== requestIdRef.current) {
        return;
      }

      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось загрузить финансовый отчет по делам.');
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
